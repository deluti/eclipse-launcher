using System.Runtime.InteropServices;
using System.Text;

namespace Eclipse;

internal enum UiCue { Click, Select, Confirm, Back, Tick, Error }

internal static class UiSounds
{
    private static Player? player;
    private static volatile bool enabled = true;
    internal static event Action<UiCue>? Requested;
    internal static int PlayedCount => Volatile.Read(ref player)?.PlayedCount ?? 0;
    internal static bool Enabled
    {
        get => enabled;
        set { enabled = value; if (!value) Volatile.Read(ref player)?.Queue(-2); }
    }
    // Called after the first window paint. Synthesis and native playback stay off the UI thread.
    internal static void Start() => player ??= new Player();
    internal static void Stop() => Interlocked.Exchange(ref player, null)?.Dispose();
    internal static void Play(UiCue cue)
    {
        if (!enabled) return;
        Requested?.Invoke(cue);
        Volatile.Read(ref player)?.Queue((int)cue);
    }

    internal static byte[] CreateWave(UiCue cue)
    {
        const int rate = 44100;
        var duration = cue switch { UiCue.Tick => .018, UiCue.Select => .028, UiCue.Confirm => .044, UiCue.Back => .032, UiCue.Error => .080, _ => .036 };
        var frequency = cue switch { UiCue.Tick => 1100, UiCue.Select => 880, UiCue.Confirm => 1000, UiCue.Back => 590, UiCue.Error => 370, _ => 740 };
        var count = (int)(rate * duration);
        using var stream = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(count * 2);
        var random = new Random(137 + (int)cue);
        var lowNoise = 0d;
        for (var index = 0; index < count; index++)
        {
            var t = index / (double)rate;
            var local = cue == UiCue.Error && t > .039 ? t - .039 : t;
            lowNoise += ((random.NextDouble() * 2 - 1) - lowNoise) * .24;
            var envelope = (1 - Math.Exp(-local * 3200)) * Math.Exp(-local * 155);
            var tail = Math.Min(1, (count - 1 - index) / (rate * .004));
            var tone = Math.Sin(2 * Math.PI * frequency * local) * .48 + Math.Sin(2 * Math.PI * frequency * 1.47 * local) * .18;
            var volume = cue == UiCue.Tick ? .095 : .15;
            var sample = (tone + lowNoise * .45) * envelope * tail * volume;
            writer.Write((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
        }
        return stream.ToArray();
    }

    private sealed class Player : IDisposable
    {
        private readonly AutoResetEvent wake = new(false);
        private readonly Thread thread;
        private readonly object gate = new();
        private int pending = -1;
        private bool stopping;
        internal int PlayedCount;
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
        internal Player()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Eclipse UI audio" };
            thread.Start();
        }
        internal void Queue(int cue)
        {
            lock (gate)
            {
                if (stopping) return;
                pending = cue; wake.Set(); // Keep only the latest action; never accumulate a backlog.
            }
        }
        private void Run()
        {
            var buffers = new List<GCHandle>();
            try
            {
                foreach (var cue in Enum.GetValues<UiCue>()) buffers.Add(GCHandle.Alloc(CreateWave(cue), GCHandleType.Pinned));
                var last = long.MinValue / 2;
                while (true)
                {
                    wake.WaitOne();
                    int next;
                    bool finish;
                    lock (gate) { finish = stopping; next = pending; pending = -1; }
                    if (finish)
                    {
                        if (next >= 0 && enabled) PlaySound(buffers[next].AddrOfPinnedObject(), IntPtr.Zero, 0x0002 | 0x0004);
                        return;
                    }
                    if (next == -2) { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); continue; }
                    if (next < 0 || !enabled) continue;
                    var now = Environment.TickCount64;
                    var interval = next == (int)UiCue.Tick ? 60 : 42;
                    if (next != (int)UiCue.Error && now - last < interval) continue;
                    last = now;
                    if (PlaySound(buffers[next].AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004)) Interlocked.Increment(ref PlayedCount);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { }
            finally
            {
                // Stop native playback before releasing the pinned WAV buffers.
                try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
                foreach (var buffer in buffers) if (buffer.IsAllocated) buffer.Free();
                lock (gate) { stopping = true; wake.Dispose(); }
            }
        }
        public void Dispose()
        {
            lock (gate) { if (stopping) return; stopping = true; wake.Set(); }
            thread.Join(100);
        }
    }
}
