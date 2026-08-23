using System.IO;
using System.Text.Json;

namespace Palon;

sealed record CallRecord(DateTime StartedUtc, int DurationSec, string? Number = null);

/// <summary>
/// Daily call stats: %LOCALAPPDATA%\Palon\calls.json, same atomic store
/// pattern as reminders. One record per finished call, kept for 90 days.
/// </summary>
static class CallStatsStore
{
    static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");
    static string FilePath => Path.Combine(Dir, "calls.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<Entry> Calls { get; set; } = new();
    }

    sealed class Entry
    {
        public DateTime StartedUtc { get; set; }
        public int DurationSec { get; set; }
        public string? Number { get; set; }
    }

    public static List<CallRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<CallRecord>();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Calls is not { } entries) throw new JsonException("no calls array");
            return entries
                .Select(e => new CallRecord(
                    DateTime.SpecifyKind(e.StartedUtc, DateTimeKind.Utc),
                    Math.Max(0, e.DurationSec),
                    e.Number))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Call stats load failed: {ex.Message}");
            return new List<CallRecord>();
        }
    }

    public static void Add(CallRecord record)
    {
        try
        {
            var all = Load();
            all.Add(record);
            var cutoff = DateTime.UtcNow - Retention;
            all.RemoveAll(c => c.StartedUtc < cutoff);
            Directory.CreateDirectory(Dir);
            var envelope = new Envelope
            {
                Calls = all.Select(c => new Entry
                {
                    StartedUtc = c.StartedUtc,
                    DurationSec = c.DurationSec,
                    Number = c.Number,
                }).ToList(),
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Call stats save failed: {ex.Message}");
            return; // don't announce a save that didn't happen
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// Appends one record per finished call by watching the engine's state
/// transitions (its events fire on the UI thread from TickAsync).
/// </summary>
sealed class CallStatsTracker : IDisposable
{
    readonly CallEngine _engine;
    CallState _lastState = CallState.Idle;
    DateTime _startedUtc;
    string? _number;

    public CallStatsTracker(CallEngine engine)
    {
        _engine = engine;
        _engine.StateChanged += OnStateChanged;
    }

    void OnStateChanged()
    {
        var state = _engine.State;
        var was = _lastState;
        _lastState = state;

        if (state == CallState.OnCall && was != CallState.OnCall)
        {
            _startedUtc = DateTime.UtcNow;
            _number = _engine.CurrentNumber;
            return;
        }
        if (was == CallState.OnCall && state != CallState.OnCall)
        {
            // Idle fires ~2 s after the real hang-up (resume grace) — close
            // enough for stats, and disable-mid-call flips immediately.
            var duration = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds;
            if (duration > 0) CallStatsStore.Add(new CallRecord(_startedUtc, duration, _number));
            _number = null;
        }
    }

    public void Dispose() => _engine.StateChanged -= OnStateChanged;
}
