using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Palon.Salesforce;

/// <summary>
/// One way to find an element. Exactly one of the targeting fields is used;
/// order of preference is <see cref="LocatorRanking"/>'s. Never an id or a
/// CSS/XPath path into component internals — those change on every load.
/// </summary>
sealed record SfLocator
{
    public string? Role { get; init; }
    public string? Name { get; init; }          // accessible name (with Role); exact after whitespace folding
    public bool NameContains { get; init; }     // match Name as a substring
    public string? AriaLabel { get; init; }
    public string? FieldApiName { get; init; }  // e.g. "Task.Subject" → [data-target-selection-name$="Task.Subject"], [field-name="Subject"]
    public string? Css { get; init; }           // last resort, attribute selectors only

    [JsonIgnore]
    public string Key => $"{Role}|{Name}|{(NameContains ? 1 : 0)}|{AriaLabel}|{FieldApiName}|{Css}";

    public override string ToString() =>
        FieldApiName is not null ? $"field {FieldApiName}"
        : Role is not null ? $"{Role} \"{Name}\""
        : AriaLabel is not null ? $"aria-label \"{AriaLabel}\""
        : $"css {Css}";
}

sealed record SfCondition
{
    public string? UrlRegex { get; init; }
    public SfLocator? Element { get; init; }     // must exist (visible)
    public string? TextContains { get; init; }   // some accessible name must contain this (vars allowed)
    /// <summary>A Lightning success toast must be visible — proof a Save went through,
    /// where generic text like the subject may already be on the page. [verify-org]</summary>
    public bool SuccessToast { get; init; }
    public int TimeoutMs { get; init; } = 8000;
}

sealed class SfStep
{
    public string Intent { get; set; } = "";
    /// <summary>click | fill | select | press | navigate | expect</summary>
    public string Method { get; set; } = "click";
    public List<string> Args { get; set; } = new();
    public List<SfLocator> Locators { get; set; } = new();
    public SfCondition? Pre { get; set; }
    public SfCondition? Post { get; set; }
    /// <summary>A click that persists data. Only runs through the approval gate; skipped in dry-run.</summary>
    public bool Commit { get; set; }
    /// <summary>Optional steps may find nothing (e.g. a tab already open).</summary>
    public bool Optional { get; set; }
    public string? LastGood { get; set; }
    public int Hits { get; set; }
    public int Heals { get; set; }
}

sealed class SfSkill
{
    public string Skill { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Org { get; set; } = "";
    /// <summary>How date inputs are typed in this org's locale; learned in teach mode. [verify-org]</summary>
    public string DateFormat { get; set; } = "dd/MM/yyyy";
    public List<SfStep> Steps { get; set; } = new();
    public string? TaughtAt { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parsed and validated; null when the JSON is unusable.</summary>
    public static SfSkill? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<SfSkill>(json, Json);
            if (s is null || string.IsNullOrWhiteSpace(s.Skill) || s.Steps.Count == 0) return null;
            foreach (var step in s.Steps)
            {
                if (step.Method is not ("click" or "fill" or "select" or "press" or "navigate" or "expect")) return null;
                if (step.Method is "click" or "fill" or "select" && step.Locators.Count == 0) return null;
                step.Locators = step.Locators.Where(l => LocatorRanking.IsUsable(l)).ToList();
            }
            return s;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>%var% substitution at replay (the cache never stores call content).</summary>
static class SfVars
{
    static readonly Regex Placeholder = new(@"%([a-zA-Z][a-zA-Z0-9_]*)%", RegexOptions.CultureInvariant);

    public static string Apply(string template, IReadOnlyDictionary<string, string> vars) =>
        Placeholder.Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static SfLocator Apply(SfLocator l, IReadOnlyDictionary<string, string> vars) => l with
    {
        Name = l.Name is null ? null : Apply(l.Name, vars),
        AriaLabel = l.AriaLabel is null ? null : Apply(l.AriaLabel, vars),
    };

    public static bool HasUnresolved(string s) => Placeholder.IsMatch(s);

    /// <summary>Formats a date the way the org's date input expects.</summary>
    public static string FormatDate(DateOnly date, string format) =>
        date.ToString(format, CultureInfo.InvariantCulture);

    static readonly string[] CandidateFormats =
    {
        "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "dd-MM-yyyy",
    };

    /// <summary>
    /// Learns the org's date input format from a value the user typed or
    /// picked in teach mode. When the true date is known the match is exact;
    /// otherwise the first format that parses wins (day-first for Israel).
    /// </summary>
    public static string? InferDateFormat(string sample, DateOnly? knownDate = null)
    {
        sample = sample.Trim();
        foreach (var f in CandidateFormats)
        {
            if (!DateOnly.TryParseExact(sample, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (knownDate is { } k && d != k) continue;
            return f;
        }
        return null;
    }
}

/// <summary>
/// Locator ordering and learning. A skill step keeps at most
/// <see cref="MaxPerStep"/> locators; a healed or taught locator goes first,
/// and the step always keeps one language-independent (field api-name)
/// locator if it ever had one.
/// </summary>
static class LocatorRanking
{
    public const int MaxPerStep = 4;

    public static bool IsUsable(SfLocator l) =>
        (l.Role is { Length: > 0 } && l.Name is { Length: > 0 })
        || l.AriaLabel is { Length: > 0 }
        || l.FieldApiName is { Length: > 0 }
        || (l.Css is { Length: > 0 } && IsSafeCss(l.Css));

    /// <summary>Attribute-only CSS: no ids, no nth-child, no descendant chains into internals.</summary>
    public static bool IsSafeCss(string css) =>
        !Regex.IsMatch(css, @"#|nth-|:has|\s>\s|\bdiv\b|\bspan\b") && css.Contains('[');

    /// <summary>Intrinsic stability, higher is better.</summary>
    public static int Stability(SfLocator l) =>
        l.FieldApiName is not null ? 40
        : l.Role is not null && !l.NameContains ? 30
        : l.Role is not null ? 25
        : l.AriaLabel is not null ? 20
        : 10;

    /// <summary>
    /// The locator to remember for an element Palon (or the user) just
    /// used successfully: its field api-name when it has one, else
    /// role+name, else aria-label. Null when nothing stable is known.
    /// </summary>
    public static SfLocator? Derive(ElementFacts e)
    {
        if (e.FieldApiName is { Length: > 0 } f && e.Role is not ("button" or "link" or "tab" or "menuitem" or "option"))
            return new SfLocator { FieldApiName = f };
        if (e.Role is { Length: > 0 } role && e.Name is { Length: > 0 } name && role is not ("generic" or "none" or "StaticText"))
            return new SfLocator { Role = role, Name = Fold(name) };
        if (e.AriaLabel is { Length: > 0 } a) return new SfLocator { AriaLabel = Fold(a) };
        return null;
    }

    /// <summary>All the stable locators an element offers, best first (teach mode keeps the extras as fallbacks).</summary>
    public static IEnumerable<SfLocator> DeriveAll(ElementFacts e)
    {
        var list = new List<SfLocator>();
        if (e.Role is { Length: > 0 } role && e.Name is { Length: > 0 } name && role is not ("generic" or "none" or "StaticText"))
            list.Add(new SfLocator { Role = role, Name = Fold(name) });
        if (e.FieldApiName is { Length: > 0 } f) list.Add(new SfLocator { FieldApiName = f });
        if (e.AriaLabel is { Length: > 0 } a && a != e.Name) list.Add(new SfLocator { AriaLabel = Fold(a) });
        return list.OrderByDescending(Stability);
    }

    /// <summary>Learned locator first, de-duplicated, capped, field api-name retained.</summary>
    public static List<SfLocator> Promote(IReadOnlyList<SfLocator> existing, SfLocator learned)
    {
        var merged = new List<SfLocator> { learned };
        merged.AddRange(existing.Where(l => l.Key != learned.Key));
        if (merged.Count <= MaxPerStep) return merged;
        var kept = merged.Take(MaxPerStep).ToList();
        if (!kept.Any(l => l.FieldApiName is not null) && merged.FirstOrDefault(l => l.FieldApiName is not null) is { } field)
            kept[^1] = field;
        return kept;
    }

    public static string Fold(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    public static bool NameMatches(SfLocator l, string? actual)
    {
        if (l.Name is null || actual is null) return false;
        var a = Fold(actual);
        var want = Fold(l.Name);
        return l.NameContains
            ? a.Contains(want, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, want, StringComparison.OrdinalIgnoreCase)
              // Lightning appends " *" / "*Required" to required-field labels.
              || string.Equals(Regex.Replace(a, @"^\*\s*|\s*\*$|\s*\*?\s*Required$|\s*\*?\s*שדה חובה$", ""), want, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What Palon knows about one element (from the AX tree plus attributes).</summary>
sealed record ElementFacts(string? Role, string? Name, string? AriaLabel, string? FieldApiName, string? Tag = null, string? Value = null);

/// <summary>
/// Skills on disk: %LOCALAPPDATA%\Palon\salesforce\skills\&lt;skill&gt;.json.
/// A missing or broken file falls back to the built-in default, which is
/// the English Lightning UI guess — teach mode replaces it with the user's own.
/// </summary>
static class SkillStore
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "salesforce", "skills");

    static readonly object Gate = new();

    public static SfSkill Load(string name)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(Dir, name + ".json");
                if (File.Exists(path) && SfSkill.FromJson(File.ReadAllText(path)) is { } s) return s;
            }
            catch (IOException)
            {
            }
            return DefaultSkills.Get(name);
        }
    }

    public static void Save(SfSkill skill)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, skill.Skill + ".json");
                if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); // one step of history
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, skill.ToJson());
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Salesforce: could not save skill {skill.Skill}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Built-in starting points (English + Hebrew labels). Every one is
/// [verify-org]: the user's layout decides; teach mode and heal refine them.
/// </summary>
static class DefaultSkills
{
    public static readonly string[] Names = { "FindRecordByPhone", "LogCall", "NewTask", "UpdateField" };

    static SfLocator B(string name) => new() { Role = "button", Name = name };
    static SfLocator R(string role, string name) => new() { Role = role, Name = name };
    static SfLocator F(string api) => new() { FieldApiName = api };

    const string RecordPage = @"/lightning/r/(Contact|Lead|Account|Opportunity)/\w{15,18}/view";

    public static SfSkill Get(string name) => name switch
    {
        "FindRecordByPhone" => new SfSkill
        {
            Skill = name,
            Steps =
            {
                new SfStep { Intent = "open global search", Method = "click",
                    Locators = { B("Search..."), B("חיפוש..."), B("Search"), new SfLocator { Css = "button[aria-label^='Search']" } },
                    Post = new SfCondition { Element = new SfLocator { Role = "searchbox", Name = "Search..." }, TimeoutMs = 5000 } },
                new SfStep { Intent = "type the phone number", Method = "fill", Args = { "%term%" },
                    Locators = { R("searchbox", "Search..."), R("searchbox", "חיפוש..."), R("combobox", "Search...") } },
                new SfStep { Intent = "run the search", Method = "press", Args = { "Enter" },
                    Post = new SfCondition { UrlRegex = @"one\.app#|/lightning/search|/lightning/r/", TimeoutMs = 10000 } },
            },
        },
        "LogCall" => new SfSkill
        {
            Skill = name,
            Steps =
            {
                new SfStep { Intent = "open the Activity tab", Method = "click", Optional = true,
                    Pre = new SfCondition { UrlRegex = RecordPage },
                    Locators = { R("tab", "Activity"), R("tab", "פעילות") } },
                new SfStep { Intent = "open Log a Call", Method = "click",
                    Pre = new SfCondition { UrlRegex = RecordPage },
                    Locators = { B("Log a Call"), B("תיעוד שיחה"), R("tab", "Log a Call"), new SfLocator { Css = "[data-target-selection-name$='LogACall']" } },
                    Post = new SfCondition { Element = F("Task.Subject"), TimeoutMs = 8000 } },
                new SfStep { Intent = "fill Subject", Method = "fill", Args = { "%subject%" },
                    Locators = { F("Task.Subject"), R("combobox", "Subject"), R("combobox", "נושא") } },
                new SfStep { Intent = "fill Comments", Method = "fill", Args = { "%comments%" },
                    Locators = { F("Task.Description"), R("textbox", "Comments"), R("textbox", "הערות") } },
                new SfStep { Intent = "fill Date", Method = "fill", Args = { "%date%" }, Optional = true,
                    Locators = { F("Task.ActivityDate"), R("textbox", "Date"), R("textbox", "תאריך") } },
                new SfStep { Intent = "save the call", Method = "click", Commit = true,
                    Locators = { B("Save"), B("שמור") },
                    Post = new SfCondition { SuccessToast = true, TimeoutMs = 10000 } },
            },
        },
        "NewTask" => new SfSkill
        {
            Skill = name,
            Steps =
            {
                new SfStep { Intent = "open the Activity tab", Method = "click", Optional = true,
                    Pre = new SfCondition { UrlRegex = RecordPage },
                    Locators = { R("tab", "Activity"), R("tab", "פעילות") } },
                new SfStep { Intent = "open New Task", Method = "click",
                    Pre = new SfCondition { UrlRegex = RecordPage },
                    Locators = { B("New Task"), B("משימה חדשה"), R("tab", "New Task"), new SfLocator { Css = "[data-target-selection-name$='NewTask']" } },
                    Post = new SfCondition { Element = F("Task.Subject"), TimeoutMs = 8000 } },
                new SfStep { Intent = "fill Subject", Method = "fill", Args = { "%subject%" },
                    Locators = { F("Task.Subject"), R("combobox", "Subject"), R("combobox", "נושא") } },
                new SfStep { Intent = "fill Due Date", Method = "fill", Args = { "%date%" },
                    Locators = { F("Task.ActivityDate"), R("textbox", "Due Date"), R("textbox", "תאריך יעד") } },
                new SfStep { Intent = "save the task", Method = "click", Commit = true,
                    Locators = { B("Save"), B("שמור") },
                    Post = new SfCondition { SuccessToast = true, TimeoutMs = 10000 } },
            },
        },
        "UpdateField" => new SfSkill
        {
            Skill = name,
            Steps =
            {
                new SfStep { Intent = "start inline edit of %field%", Method = "click",
                    Pre = new SfCondition { UrlRegex = RecordPage },
                    Locators = { B("Edit %field%"), B("ערוך %field%") } },
                new SfStep { Intent = "choose %value% for %field%", Method = "select", Args = { "%value%" },
                    Locators = { R("combobox", "%field%"), new SfLocator { Role = "combobox", Name = "%field%", NameContains = true } } },
                new SfStep { Intent = "save the field", Method = "click", Commit = true,
                    Locators = { B("Save"), B("שמור") },
                    Post = new SfCondition { SuccessToast = true, TextContains = "%value%", TimeoutMs = 10000 } },
            },
        },
        _ => throw new ArgumentException("unknown skill " + name),
    };
}
