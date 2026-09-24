namespace Palon.Agentic;

/// <summary>What kind of proactive moment a nudge is — drives its icon and priority.</summary>
enum NudgeKind { Insight, Suggestion, Reminder, Celebration }

/// <summary>
/// One proactive line from Palon ("PPC brings you ₪429 on average", "Maya went quiet — draft a follow-up?").
/// Act runs the offered action; null means the nudge is informational.
/// </summary>
sealed record Nudge(string Id, NudgeKind Kind, string Text, string? ActionLabel, Func<Task>? Act, DateTime AtLocal);

/// <summary>
/// The shared bus between the proactive engine and the surfaces (Now window, dock).
/// Raise from any thread; subscribers are invoked on the thread that raised — surfaces marshal to their dispatcher.
/// </summary>
static class NudgeHub
{
    static readonly object Gate = new();
    static readonly List<Nudge> ActiveList = new();

    public static event Action<Nudge>? Raised;
    public static event Action<string>? Dismissed;

    public static IReadOnlyList<Nudge> Active { get { lock (Gate) return ActiveList.ToList(); } }

    public static void Raise(Nudge nudge)
    {
        lock (Gate)
        {
            ActiveList.RemoveAll(n => n.Id == nudge.Id);
            ActiveList.Add(nudge);
        }
        Raised?.Invoke(nudge);
    }

    public static void Dismiss(string id)
    {
        lock (Gate) ActiveList.RemoveAll(n => n.Id == id);
        Dismissed?.Invoke(id);
    }
}

/// <summary>What a typed command would do, shown before it runs ("דני מחר ב-11" → callback preview).</summary>
sealed record CommandPreview(string Kind, string Title, string Detail, string ConfirmLabel, Func<CancellationToken, Task<string?>> Execute);

/// <summary>
/// The command bar's brain. PreviewAsync turns free text into a previewable action (or null → ask Palon);
/// Suggest returns quick completions for the typed prefix. Implemented by the agentic layer.
/// </summary>
static class CommandRouter
{
    public static Func<string, CancellationToken, Task<CommandPreview?>> PreviewAsync { get; set; } = (_, _) => Task.FromResult<CommandPreview?>(null);
    public static Func<string, IReadOnlyList<string>> Suggest { get; set; } = _ => Array.Empty<string>();

    /// <summary>Raised after a command ran that can be reversed: (what was done, undo). The UI shows an
    /// Undo toast; the undo returns the Hebrew line to show once reversed.</summary>
    public static event Action<string, Func<Task<string>>>? UndoOffered;

    public static void OfferUndo(string label, Func<Task<string>> undo) => UndoOffered?.Invoke(label, undo);
}
