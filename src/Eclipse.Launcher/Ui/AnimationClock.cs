using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Eclipse;

// Native timer sleeps between frames. No busy-wait and no global timer-resolution changes.
internal sealed class AnimationClock : IDisposable
{
    internal const int FramesPerSecond = 120;
    private readonly Control owner;
    private readonly Action<double> frame;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent changed = new(false);
    private readonly Thread worker;
    private volatile bool active;
    private volatile bool disposed;
    private int queued;
    private long lastFrame;
    internal bool IsRunning => active && !disposed;
    internal Exception? Failure { get; private set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    private sealed class TimerHandle : WaitHandle
    {
        internal TimerHandle(SafeWaitHandle handle) { SafeWaitHandle = handle; }
    }
    internal AnimationClock(Control owner, Action<double> frame)
    {
        this.owner = owner; this.frame = frame;
        worker = new Thread(Run) { IsBackground = true, Name = "Eclipse 120 Hz animation" };
        worker.Start();
    }
    internal void SetActive(bool value)
    {
        if (disposed || active == value) return;
        active = value; Interlocked.Exchange(ref lastFrame, 0); changed.Set();
    }
    private void Run()
    {
        try
        {
            var native = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x001F0003);
            if (native.IsInvalid) { native.Dispose(); native = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x001F0003); }
            if (native.IsInvalid) { native.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            using var timer = new TimerHandle(native);
            WaitHandle[] waits = [stop, changed, timer];
            WaitHandle[] paused = [stop, changed];
            var period = (double)Stopwatch.Frequency / FramesPerSecond;
            var next = (double)Stopwatch.GetTimestamp();
            while (!disposed)
            {
                if (!active)
                {
                    if (WaitHandle.WaitAny(paused) == 0) return;
                    next = Stopwatch.GetTimestamp() + period;
                    continue;
                }
                var now = Stopwatch.GetTimestamp();
                var remaining = (next - now) / Stopwatch.Frequency;
                if (remaining > 0)
                {
                    var due = -Math.Max(1, (long)(remaining * 10_000_000));
                    if (!SetWaitableTimer(native, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var signal = WaitHandle.WaitAny(waits);
                    if (signal == 0) return;
                    if (signal == 1) { next = Stopwatch.GetTimestamp() + period; continue; }
                }
                if (active) PostFrame();
                next += period;
                now = Stopwatch.GetTimestamp();
                if (next < now - period) next = now + period;
            }
        }
        catch (Exception ex) { Failure = ex; active = false; }
    }
    private void PostFrame()
    {
        if (Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
        try
        {
            owner.BeginInvoke((Action)(() =>
            {
                // The callback is executing now; permit one upcoming frame to wait behind it.
                // Resetting after rendering drops timer ticks even when the UI can keep up on average.
                Interlocked.Exchange(ref queued, 0);
                if (disposed || !active || owner.IsDisposed || !owner.IsHandleCreated) return;
                var now = Stopwatch.GetTimestamp();
                var previous = Interlocked.Exchange(ref lastFrame, now);
                var elapsed = previous == 0 ? 1d / FramesPerSecond : (double)(now - previous) / Stopwatch.Frequency;
                frame(Math.Clamp(elapsed, 0, .05));
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; active = false; stop.Set(); changed.Set();
        if (worker.Join(500)) { stop.Dispose(); changed.Dispose(); }
    }
}
