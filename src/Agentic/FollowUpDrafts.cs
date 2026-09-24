using System.Text;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.Agentic;

/// <summary>A follow-up message Palon drafted, keyed to the note it was written from.</summary>
sealed record CachedDraft(string ClientKey, string NoteId, string Text, DateTime CreatedUtc, bool Revive);

/// <summary>
/// Personalized follow-up drafts on top of the existing FollowUp generation
/// (AiChat.FollowUpAsync). The prompt input is the latest call note plus what
/// Palon remembers about the client and, for a quiet lead, how long it has been.
/// Drafts are cached per (client, note) for a few days so a nudge, the command
/// bar and the agent tool never pay for the same draft twice.
/// </summary>
static class FollowUpDrafts
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(4);
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Swappable for tests: (noteText) → draft.</summary>
    internal static Func<string, CancellationToken, Task<string?>> Generate { get; set; } = AiChat.FollowUpAsync;

    public static CachedDraft? Cached(string clientKey, string noteId) =>
        AgenticState.Load().Drafts.LastOrDefault(d => d.ClientKey == clientKey && d.NoteId == noteId && DateTime.UtcNow - d.CreatedUtc < MaxAge);

    /// <summary>The prompt input: the note, the facts, and the gap since the call.</summary>
    public static string BuildInput(WorkSnapshot s, ClientCard card, CallNote note, string? extraInstruction = null)
    {
        var sb = new StringBuilder();
        var callLocal = note.StartedUtc.ToLocalTime();
        var days = (int)(s.Now.Date - callLocal.Date).TotalDays;
        sb.Append($"Client: {TemplateFill.FirstName(card.Name)}\n");
        sb.Append($"Call on {callLocal:yyyy-MM-dd HH:mm} ({He.Duration(note.DurationSec)} min).\n");
        sb.Append((note.Summary ?? note.Transcript).Trim()).Append('\n');
        var facts = s.FactsFor(card).Take(8).ToList();
        if (facts.Count > 0)
            sb.Append("\nWhat we know about the client:\n").Append(string.Join("\n", facts.Select(f => "- " + f.Text))).Append('\n');
        var signals = BuyingSignals.Detect(note);
        if (signals.Count > 0)
            sb.Append("\nThe client showed interest: ").Append(string.Join(", ", signals.Select(x => x.Label))).Append(".\n");
        if (days >= 2)
            sb.Append($"\nIMPORTANT: {days} days have passed since this call with no contact. Write a warm, short re-engagement " +
                      "message that references one concrete thing from the call and proposes a specific next step (a short call today or tomorrow). " +
                      "Do not apologize and do not pressure.\n");
        if (!string.IsNullOrWhiteSpace(extraInstruction)) sb.Append("\nUser's request: ").Append(extraInstruction!.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The draft for this client's latest note — from cache, else generated (one at a time).
    /// Returns (text, error); error is Hebrew and user-facing.
    /// </summary>
    public static async Task<(string? Text, string? Error)> GetOrCreateAsync(WorkSnapshot s, ClientCard card, string? instruction, CancellationToken ct)
    {
        var note = s.LatestNote(card);
        if (note is null) return (null, $"אין לי שיחה מתועדת עם {card.Name} — אין ממה לנסח.");
        if (instruction is null && Cached(card.Key, note.Id) is { } hit) return (hit.Text, null);
        if (!AiChat.HasKey) return (null, "אין מפתח AI מוגדר — הוסף מפתח בהגדרות וננסח.");
        await OneAtATime.WaitAsync(ct);
        try
        {
            if (instruction is null && Cached(card.Key, note.Id) is { } again) return (again.Text, null);
            var text = (await Generate(BuildInput(s, card, note, instruction), ct))?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return (null, AiChat.DescribeFailureHe(AiChat.LastError));
            var days = (int)(s.Now.Date - note.StartedUtc.ToLocalTime().Date).TotalDays;
            if (instruction is null)
                AgenticState.Mutate(st =>
                {
                    st.Drafts.RemoveAll(d => d.ClientKey == card.Key);
                    st.Drafts.Add(new CachedDraft(card.Key, note.Id, text!, DateTime.UtcNow, days >= 2));
                });
            return (text, null);
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
