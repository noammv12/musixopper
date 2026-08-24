using System.Text;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>
/// Short-term conversational memory plus the live context block. A session
/// spans a few minutes of back-and-forth ("ומה מחר?" works); after the TTL
/// it quietly starts fresh. The context block is rebuilt every turn from
/// local data only — time, call state, today's stats, the current caller's
/// last note — which is what lets Palon answer about "him" mid-call.
/// </summary>
sealed class AssistantSession
{
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);
    const int MaxExchanges = 4;
    const int MaxAnswerChars = 600; // memory, not archive — keep turns cheap

    readonly List<(string Question, string Answer)> _exchanges = new();
    readonly Func<CallState> _callState;
    readonly Func<string?> _currentNumber;
    DateTime _lastExchangeUtc;

    public AssistantSession(Func<CallState> callState, Func<string?> currentNumber)
    {
        _callState = callState;
        _currentNumber = currentNumber;
    }

    public IReadOnlyList<(string Question, string Answer)> Exchanges
    {
        get
        {
            if (DateTime.UtcNow - _lastExchangeUtc > Ttl) _exchanges.Clear();
            return _exchanges;
        }
    }

    public void Record(string question, string answer)
    {
        if (DateTime.UtcNow - _lastExchangeUtc > Ttl) _exchanges.Clear();
        if (answer.Length > MaxAnswerChars) answer = answer[..MaxAnswerChars] + "…";
        _exchanges.Add((question, answer));
        if (_exchanges.Count > MaxExchanges) _exchanges.RemoveAt(0);
        _lastExchangeUtc = DateTime.UtcNow;
    }

    /// <summary>Persona + live context + saved commands, rebuilt per turn.</summary>
    public string BuildSystemPrompt()
    {
        var sb = new StringBuilder(
            "You are Palon, the user's personal AI aide — composed, precise, quietly capable, in " +
            "the register of a trusted butler (think J.A.R.V.I.S.): courteous, direct, a dry touch " +
            "of wit when it fits, never chatty. The user is a busy salesperson; the input is a " +
            "voice transcript and your reply may be spoken out loud.\n" +
            "RULES: The user speaks Hebrew (occasionally English), and the transcript comes from " +
            "imperfect speech recognition — if it reads as any other language, or as nonsense, it " +
            "is almost certainly Hebrew misheard: reinterpret it phonetically as Hebrew and act on " +
            "that meaning (e.g. a transcript like 'La Rabia de Argentina' is 'הבירה של ארגנטינה' — " +
            "answer Buenos Aires). Reply in Hebrew (male grammatical forms for yourself); reply in " +
            "English only when the user clearly spoke English. At most 2 short sentences unless " +
            "clearly asked for more. Plain text only — no emoji, no markdown. Be decisive: never " +
            "ask a clarifying question, never reply with a generic offer to help. A question gets " +
            "answered from your own knowledge — never turn the literal transcript into a web " +
            "search; open a search-results page only when the user explicitly asked to search " +
            "('חפש...', 'search for...'). Use the tools when they serve the request; answer " +
            "directly when they don't. If asked to open something that matches no saved command " +
            "and no well-known site, say in one sentence to add it under Commands. Only if you " +
            "truly cannot recover the meaning, say you didn't catch it.\n");

        sb.Append("\nCONTEXT\nNow: ").Append(DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm")).Append(" (local)");
        AppendCallState(sb);
        AppendToday(sb);

        var commands = CommandStore.Load();
        sb.Append(commands.Count == 0
            ? "\nThe user has no saved commands."
            : "\nSaved commands:");
        foreach (var command in commands)
            sb.Append($"\n- id={command.Id} label=\"{command.Label}\"");
        return sb.ToString();
    }

    void AppendCallState(StringBuilder sb)
    {
        if (_callState() != CallState.OnCall)
        {
            sb.Append("\nCall state: not on a call.");
            return;
        }
        var number = _currentNumber();
        sb.Append("\nCall state: ON A CALL right now").Append(number is null ? "." : $" with {number}.");
        if (number is null) return;
        try
        {
            var lastNote = NotesStore.Load()
                .Where(n => PhoneMatch.Same(n.Number, number))
                .MaxBy(n => n.StartedUtc);
            if (lastNote is null) return;
            var body = lastNote.Summary ?? lastNote.Transcript;
            if (body.Length > 350) body = body[..350] + "…";
            sb.Append($"\nLast note about this caller ({lastNote.StartedUtc.ToLocalTime():d MMM}): {body}");
        }
        catch (Exception ex)
        {
            Log.Write($"Session context: caller lookup failed: {ex.Message}");
        }
    }

    static void AppendToday(StringBuilder sb)
    {
        try
        {
            var today = CallStatsStore.Load()
                .Where(c => c.StartedUtc.ToLocalTime().Date == DateTime.Now.Date)
                .ToList();
            if (today.Count > 0)
                sb.Append($"\nToday so far: {today.Count} call(s), " +
                          $"{TimeSpan.FromSeconds(today.Sum(c => (long)c.DurationSec)):h\\:mm\\:ss} on the line.");
            var pending = ReminderStore.Load().Count(r => r.State == ReminderState.Pending);
            if (pending > 0) sb.Append($"\nPending reminders: {pending} (list_reminders for details).");
        }
        catch (Exception ex)
        {
            Log.Write($"Session context: stats lookup failed: {ex.Message}");
        }
    }
}
