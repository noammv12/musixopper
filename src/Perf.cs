using System.Diagnostics;
using System.IO;

namespace Palon;

/// <summary>Builds each value on first use and keeps it (lazy side-sheet screens).</summary>
sealed class LazyRegistry<TKey, TValue> where TKey : notnull
{
    readonly Dictionary<TKey, Func<TValue>> _factories = new();
    readonly Dictionary<TKey, TValue> _built = new();

    public void Register(TKey key, Func<TValue> factory)
    {
        _factories[key] = factory;
        _built.Remove(key);
    }

    /// <summary>Registers an already-built value.</summary>
    public void Set(TKey key, TValue value)
    {
        _factories[key] = () => value;
        _built[key] = value;
    }

    public bool IsBuilt(TKey key) => _built.ContainsKey(key);

    public int BuiltCount => _built.Count;

    public TValue this[TKey key] => Get(key);

    public TValue Get(TKey key)
    {
        if (_built.TryGetValue(key, out var v)) return v;
        if (!_factories.TryGetValue(key, out var make)) throw new KeyNotFoundException(key.ToString());
        v = make();
        _built[key] = v;
        return v;
    }

    /// <summary>The built value, without building it.</summary>
    public bool TryGetBuilt(TKey key, out TValue value) => _built.TryGetValue(key, out value!);
}

/// <summary>
/// Lightweight performance diagnostics: one "perf:" log line every 5 minutes
/// (working set, GC heap, gen2 count, live avatars, frame clock on/off).
/// <c>Palon.exe perf</c> prints the latest lines from the log.
/// </summary>
static class Perf
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    public const string Tag = "perf:";

    /// <summary>Supplied by the UI layer: (live avatars, avatar clock running).</summary>
    public static Func<(int Avatars, bool ClockOn)>? AvatarProbe;

    static Timer? _timer;

    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => Log.Write(Snapshot()), null, TimeSpan.FromMinutes(1), Every);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static string Snapshot()
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        (int avatars, bool clock) = (0, false);
        try { if (AvatarProbe is { } probe) (avatars, clock) = probe(); } catch { }
        return Format(p.WorkingSet64, p.PrivateMemorySize64, GC.GetTotalMemory(false), gc.HeapSizeBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), avatars, clock, p.Threads.Count);
    }

    public static string Format(long workingSet, long privateBytes, long managed, long heap,
        int gen0, int gen1, int gen2, int avatars, bool clockOn, int threads) =>
        $"{Tag} ws={Mb(workingSet)} private={Mb(privateBytes)} managed={Mb(managed)} gcheap={Mb(heap)} " +
        $"gc0={gen0} gc1={gen1} gc2={gen2} threads={threads} avatars={avatars} avatarClock={(clockOn ? "on" : "off")}";

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0}MB";

    /// <summary>The last <paramref name="max"/> perf lines of a log (for the CLI).</summary>
    public static List<string> LastLines(IEnumerable<string> logLines, int max = 12)
    {
        var q = new Queue<string>();
        foreach (var l in logLines)
        {
            if (!l.Contains(Tag, StringComparison.Ordinal)) continue;
            q.Enqueue(l);
            if (q.Count > max) q.Dequeue();
        }
        return q.ToList();
    }
}
