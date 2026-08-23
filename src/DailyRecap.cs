using System.Text;
using Palon.Notes;

namespace Palon;

/// <summary>
/// Assembles today's raw activity — calls, note summaries, pending
/// reminders — into one text block. The AI turns it into the spoken/written
/// recap; this stays pure data so the same block feeds the agent tool and
/// the Stats panel button.
/// </summary>
static class DailyRecap
{
    const int MaxNotes = 8;
    const int MaxCharsPerNote = 300;
    const int MaxReminders = 5;

    public static string BuildData() =>
        Build(NotesStore.Load(), CallStatsStore.Load(), ReminderStore.Load(), DateTime.Now);

    internal static string Build(
        List<CallNote> notes, List<CallRecord> calls, List<Reminder> reminders, DateTime nowLocal)
    {
        var sb = new StringBuilder();
        var today = calls.Where(c => c.StartedUtc.ToLocalTime().Date == nowLocal.Date).ToList();
        sb.Append(today.Count == 0
            ? "Calls today: none."
            : $"Calls today: {today.Count}, total {TimeSpan.FromSeconds(today.Sum(c => (long)c.DurationSec)):h\\:mm\\:ss} on the line.");

        var todaysNotes = notes
            .Where(n => n.StartedUtc.ToLocalTime().Date == nowLocal.Date)
            .OrderBy(n => n.StartedUtc)
            .ToList();
        if (todaysNotes.Count > 0)
        {
            sb.Append("\n\nCall notes today:");
            foreach (var note in todaysNotes.TakeLast(MaxNotes))
            {
                var body = (note.Summary ?? note.Transcript).Trim();
                if (body.Length > MaxCharsPerNote) body = body[..MaxCharsPerNote] + "…";
                sb.Append($"\n[{note.StartedUtc.ToLocalTime():HH:mm}]")
                  .Append(note.Number is { } number ? $" {number}" : "")
                  .Append(": ").Append(body.Replace("\n", " / "));
            }
            if (todaysNotes.Count > MaxNotes) sb.Append($"\n(+{todaysNotes.Count - MaxNotes} earlier notes)");
        }

        var pending = reminders
            .Where(r => r.State == ReminderState.Pending)
            .OrderBy(r => r.DueAtUtc)
            .Take(MaxReminders)
            .ToList();
        if (pending.Count > 0)
        {
            sb.Append("\n\nPending reminders:");
            foreach (var reminder in pending)
                sb.Append($"\n- {reminder.DisplayLabel} — {reminder.DueAtUtc.ToLocalTime():ddd d MMM HH:mm}");
        }
        return sb.ToString();
    }
}
