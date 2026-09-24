using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palon.Terminal;

/// <summary>How the client's name goes into a template: Replace swaps it in
/// right after <see cref="MessageTemplate.Head"/> ("היי !" → "היי דני!");
/// Prepend puts a "היי דני!" line above the text.</summary>
enum TemplateMode { Prepend, Replace }

sealed class MessageTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "";
    /// <summary>When to send it — one short line under the title.</summary>
    public string Tag { get; set; } = "";
    public string Region { get; set; } = "";
    public TemplateMode Mode { get; set; } = TemplateMode.Prepend;
    public string Head { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Bumped on every saved change to the wording; sends are logged
    /// against it so edit mining compares like with like.</summary>
    public int Version { get; set; } = 1;
    /// <summary>Earlier versions, oldest first.</summary>
    public List<TemplateRevision> History { get; set; } = new();

    public MessageTemplate Clone() => new()
    {
        Id = Id, Title = Title, Tag = Tag, Region = Region, Mode = Mode, Head = Head, Text = Text,
        Version = Version, History = History.ToList(),
    };
}

/// <summary>A past version of a template, kept so any change can be undone.</summary>
sealed record TemplateRevision(int Version, string Title, string Text, DateTime SavedUtc, string Reason = "");

static class TemplateVersions
{
    public const int MaxHistory = 30;

    /// <summary>Pure. Saving over <paramref name="old"/>: when the wording or
    /// title changed, the old one goes to history and the version goes up.</summary>
    public static MessageTemplate Next(MessageTemplate? old, MessageTemplate updated, DateTime nowUtc, string reason = "")
    {
        var t = updated.Clone();
        if (old is null) return t;
        t.History = old.History.ToList();
        t.Version = old.Version;
        if (old.Text != t.Text || old.Title != t.Title)
        {
            t.History.Add(new TemplateRevision(old.Version, old.Title, old.Text, nowUtc, reason));
            if (t.History.Count > MaxHistory) t.History.RemoveRange(0, t.History.Count - MaxHistory);
            t.Version = old.Version + 1;
        }
        return t;
    }
}

/// <summary>A filled template split around the inserted name, so the
/// preview can highlight exactly what Palon typed in.</summary>
readonly record struct TemplateParts(string Pre, string Name, string Post)
{
    public string Full => Pre + Name + Post;
}

static class TemplateFill
{
    /// <summary>Pure. No name → the text untouched. Replace mode slots the
    /// name right after Head ("היי !" → "היי דני!", "ברוך הבא!" → "ברוך הבא
    /// דני!"); if the text no longer starts with Head, falls back to Prepend.</summary>
    public static TemplateParts Parts(MessageTemplate t, string? name)
    {
        var nm = (name ?? "").Trim();
        if (nm.Length == 0) return new TemplateParts("", "", t.Text);
        var head = t.Head.TrimEnd();
        if (t.Mode == TemplateMode.Replace && head.Length > 0 && t.Text.StartsWith(head, StringComparison.Ordinal))
            return new TemplateParts(head + " ", nm, t.Text[head.Length..].TrimStart(' '));
        return new TemplateParts("היי ", nm, "!\n" + t.Text);
    }

    public static string Fill(MessageTemplate t, string? name) => Parts(t, name).Full;

    /// <summary>First word of a name — "דני לוי" → "דני". Templates greet by first name.</summary>
    public static string FirstName(string? fullName)
    {
        var s = (fullName ?? "").Trim();
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>
    /// Which template fits what was said on a call, by plain keywords in the
    /// summary/transcript: questionnaire → deposit → Israel/Pro opening.
    /// Null when nothing clearly fits (better no button than a wrong one).
    /// </summary>
    public static MessageTemplate? Suggest(IReadOnlyList<MessageTemplate> templates, string? callText)
    {
        var text = callText ?? "";
        if (text.Length == 0 || templates.Count == 0) return null;
        MessageTemplate? ById(string id) => templates.FirstOrDefault(t => t.Id == id);
        if (ContainsAny(text, "שאלון", "questionnaire")) return ById("questionnaire");
        if (ContainsAny(text, "פרטי הפקדה", "להפקיד", "העברה", "deposit")) return ById("deposit-pro");
        if (ContainsAny(text, "קולמקס ישראל", "פיקוח ישראלי", "ישראל")) return ById("open-israel");
        if (ContainsAny(text, "פרו", "pro", "שורט", "OTC")) return ById("open-pro");
        return null;
    }

    static bool ContainsAny(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// %LOCALAPPDATA%\Palon\templates.json — seeded with the user's five
/// templates on first read. Same atomic write as the other stores; an
/// unreadable file is never overwritten.
/// </summary>
static class TemplatesStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string? PathOverride { get; set; }
    static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "templates.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<MessageTemplate> Templates { get; set; } = new();
    }

    /// <summary>All templates in display order; seeds on first run. Null = unreadable file.</summary>
    static List<MessageTemplate>? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return TemplateSeeds.Create();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Templates is null) throw new JsonException("no templates array");
            envelope.Templates.RemoveAll(t => t is null);
            foreach (var t in envelope.Templates) t.History ??= new();
            return envelope.Templates;
        }
        catch (Exception ex)
        {
            Log.Write($"Templates load failed: {ex.Message}");
            return null;
        }
    }

    public static List<MessageTemplate> Load() => Read() ?? TemplateSeeds.Create();

    public static bool Save(MessageTemplate template, string reason = "") => Mutate(list =>
    {
        var i = list.FindIndex(t => t.Id == template.Id);
        if (i >= 0) list[i] = TemplateVersions.Next(list[i], template, DateTime.UtcNow, reason);
        else list.Add(template);
    });

    public static bool Remove(string id) => Mutate(list => list.RemoveAll(t => t.Id == id));

    /// <summary>Puts a removed template back at its old position (undo).</summary>
    public static bool Restore(MessageTemplate template, int index) => Mutate(list =>
    {
        list.RemoveAll(t => t.Id == template.Id);
        list.Insert(Math.Clamp(index, 0, list.Count), template);
    });

    static bool Mutate(Action<List<MessageTemplate>> change)
    {
        var list = Read();
        if (list is null) return false;
        change(list);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Envelope { Templates = list }, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Templates save failed: {ex.Message}");
            return false;
        }
        Changed?.Invoke();
        return true;
    }
}
