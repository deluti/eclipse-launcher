using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Eclipse;

internal sealed class PlaytimeCounter
{
    private sealed class State { public State() { } public double TotalSeconds { get; set; } }
    private readonly object gate = new();
    private readonly string file;
    private readonly TimeProvider clock;
    private double savedSeconds;
    private long? started;
    private ITimer? checkpoint;
    internal double TotalHours { get { lock (gate) return Seconds / 3600; } }
    private double Seconds => savedSeconds + (started is { } timestamp ? clock.GetElapsedTime(timestamp).TotalSeconds : 0);
    internal static string Format(double hours) => hours.ToString("F1", L.Culture) + L.T(" ч");

    internal PlaytimeCounter(AppPaths paths, TimeProvider? clock = null)
    {
        file = paths.FilePath("playtime.json");
        this.clock = clock ?? TimeProvider.System;
        try
        {
            var value = JsonStore.Read<State>(file)?.TotalSeconds ?? 0;
            savedSeconds = double.IsFinite(value) && value >= 0 ? value : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    internal IDisposable BeginSession()
    {
        lock (gate)
        {
            if (started != null) throw new InvalidOperationException(L.T("Сессия Minecraft уже учитывается."));
            started = clock.GetTimestamp();
            checkpoint = clock.CreateTimer(_ => Save(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return new Session(this);
        }
    }
    internal void Save()
    {
        lock (gate)
        {
            try { JsonStore.Write(file, new State { TotalSeconds = Seconds }); }
            // A full/read-only disk must not terminate a running Minecraft process.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
        }
    }
    private void EndSession()
    {
        lock (gate)
        {
            if (started == null) return;
            savedSeconds = Seconds;
            started = null;
            checkpoint?.Dispose(); checkpoint = null;
            Save();
        }
    }
    private sealed class Session(PlaytimeCounter owner) : IDisposable
    {
        private PlaytimeCounter? counter = owner;
        public void Dispose() => Interlocked.Exchange(ref counter, null)?.EndSession();
    }
}
