using System.Diagnostics;
using System.Windows.Threading;

namespace Palon;

/// <summary>
/// Watches the callback store and delivers due callbacks to the dock.
/// Delivery is held while a call is live and while the dock is hidden for
/// fullscreen; a callback fires once (snooze re-arms it). Anything that came
/// due while Palon was closed fires shortly after launch, marked missed.
/// </summary>
sealed class CallbackScheduler : IDisposable
{
    readonly CallEngine _engine;
    readonly DispatcherTimer _timer;
    readonly HashSet<string> _delivered = new();
    List<Callback> _cache;

    /// <summary>Raised on the UI thread with the due callback and whether it was missed.</summary>
    public event Action<Callback, bool>? CallbackDue;

    public CallbackScheduler(CallEngine engine)
    {
        _engine = engine;
        _cache = CallbackStore.Load();
        CallbackStore.Changed += OnStoreChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        // First check shortly after launch fires anything missed while off.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            Check();
        };
        first.Start();
    }

    void OnStoreChanged() => _cache = CallbackStore.Load();

    void Check()
    {
        if (_engine.State == CallState.OnCall) return; // never interrupt a live call
        var now = DateTime.UtcNow;
        foreach (var callback in _cache)
        {
            if (!callback.IsActive) continue;
            if (callback.DueAtUtc > now) continue;
            // Keyed by id + due so a snooze from any surface re-arms it.
            if (!_delivered.Add(DeliveryKey(callback))) continue;
            CallbackDue?.Invoke(callback, CallbackPlanner.IsMissed(callback, now));
        }
    }

    static string DeliveryKey(Callback callback) => $"{callback.Id}@{callback.DueAtUtc.Ticks}";

    /// <summary>Opens the link (if any) and marks it done.</summary>
    public void Open(Callback callback)
    {
        try
        {
            // Text-only callbacks have nothing to open — "Open" is just "Done".
            if (callback.HasUrl && CallbackStore.IsValidUrl(callback.Url))
                Process.Start(new ProcessStartInfo(callback.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Launch failed (broken browser association?) — don't consume the
            // callback; re-arm it shortly instead of marking it Done.
            Log.Write($"Callback open failed: {ex.Message}");
            CallbackStore.Mutate(callback.Id, c => c with { DueAtUtc = DateTime.UtcNow.AddMinutes(2) });
            return;
        }
        Done(callback);
    }

    public void Done(Callback callback) =>
        CallbackStore.Mutate(callback.Id, c => CallbackPlanner.MarkDone(c, DateTime.UtcNow));

    public void Snooze(Callback callback) => Snooze(callback, SnoozeKind.TenMinutes);

    public void Snooze(Callback callback, SnoozeKind kind) =>
        CallbackStore.Mutate(callback.Id, c => CallbackPlanner.Snooze(c, kind, DateTime.Now));

    public void Dismiss(Callback callback) =>
        CallbackStore.Mutate(callback.Id, c => CallbackPlanner.Cancel(c, DateTime.UtcNow));

    public void Dispose()
    {
        _timer.Stop();
        CallbackStore.Changed -= OnStoreChanged;
    }
}
