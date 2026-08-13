using System.Diagnostics;
using System.Windows.Threading;

namespace Bridget;

/// <summary>
/// Watches the reminder store and delivers due reminders to the dock.
/// Delivery is held while a call is live and while the dock is hidden for
/// fullscreen; a reminder fires once (snooze re-arms it).
/// </summary>
sealed class ReminderScheduler : IDisposable
{
    static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(2);
    static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(10);

    readonly CallEngine _engine;
    readonly DispatcherTimer _timer;
    readonly HashSet<string> _delivered = new();
    List<Reminder> _cache;

    /// <summary>Raised on the UI thread with the due reminder and whether it was missed.</summary>
    public event Action<Reminder, bool>? ReminderDue;

    public ReminderScheduler(CallEngine engine)
    {
        _engine = engine;
        _cache = ReminderStore.Load();
        ReminderStore.Changed += OnStoreChanged;

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

    void OnStoreChanged() => _cache = ReminderStore.Load();

    void Check()
    {
        if (_engine.State == CallState.OnCall) return; // never interrupt a live call
        var now = DateTime.UtcNow;
        foreach (var reminder in _cache)
        {
            if (reminder.State != ReminderState.Pending) continue;
            if (reminder.DueAtUtc > now) continue;
            if (!_delivered.Add(reminder.Id)) continue;
            var missed = now - reminder.DueAtUtc > MissedThreshold;
            ReminderDue?.Invoke(reminder, missed);
        }
    }

    public void Open(Reminder reminder)
    {
        try
        {
            if (ReminderStore.IsValidUrl(reminder.Url))
                Process.Start(new ProcessStartInfo(reminder.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Launch failed (broken browser association?) — don't consume the
            // reminder; re-arm it shortly instead of marking it Done.
            Log.Write($"Reminder open failed: {ex.Message}");
            _delivered.Remove(reminder.Id);
            ReminderStore.Update(reminder with { DueAtUtc = DateTime.UtcNow.AddMinutes(2) });
            return;
        }
        ReminderStore.Update(reminder with { State = ReminderState.Done });
    }

    public void Snooze(Reminder reminder)
    {
        _delivered.Remove(reminder.Id);
        ReminderStore.Update(reminder with { DueAtUtc = DateTime.UtcNow + SnoozeDelay });
    }

    public void Dismiss(Reminder reminder)
    {
        ReminderStore.Update(reminder with { State = ReminderState.Dismissed });
    }

    public void Dispose()
    {
        _timer.Stop();
        ReminderStore.Changed -= OnStoreChanged;
    }
}
