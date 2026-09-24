using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Palon.Terminal;

/// <summary>
/// One copy of a message out of Palon. <see cref="TemplateId"/> is null for
/// a free-text message typed in Palon's editor. <see cref="FinalText"/> is set
/// only when the user edited the text inside Palon before copying (edits made
/// later in WhatsApp are invisible to Palon).
/// </summary>
sealed record TemplateSend(
    string Id,
    DateTime AtUtc,
    string? TemplateId,
    int Version,
    string? Name,
    string? Phone,
    string FilledText,
    string? FinalText = null,
    string Surface = "")
{
    [JsonIgnore] public string SentText => FinalText ?? FilledText;
    [JsonIgnore] public bool Edited => FinalText is not null && FinalText != FilledText;
}

enum ProposalKind { Edit, NewTemplate }

/// <summary>A suggestion Palon shows on the Templates screen. The user
/// accepts, edits, or rejects; <see cref="Key"/> is remembered either way so
/// the same suggestion is never shown twice.</summary>
sealed record TemplateProposal(
    ProposalKind Kind,
    string Key,
    string? TemplateId,
    string Before,
    string After,
    int Count);

/// <summary>Per-template outcome numbers; only produced once a template has
/// <see cref="TemplateLearning.MinUsesForStats"/> uses.</summary>
readonly record struct TemplateStats(int Uses, int Deposits, int NextCalls)
{
    public double Rate => Uses == 0 ? 0 : (double)Deposits / Uses;

    /// <summary>Wilson 95% lower bound — a cautious rate for small samples.</summary>
    public double WilsonLower
    {
        get
        {
            if (Uses == 0) return 0;
            const double z = 1.96;
            double n = Uses, p = Rate;
            var den = 1 + z * z / n;
            var centre = p + z * z / (2 * n);
            var margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n));
            return Math.Max(0, (centre - margin) / den);
        }
    }
}

/// <summary>What happened after a send: a deposit for that client within
/// 14 days (from the sales book) and/or another call with that number.</summary>
readonly record struct SendOutcome(bool Deposit, bool NextCall);

/// <summary>
/// Human-in-the-loop template learning — pure logic. Everything is local:
/// sends are logged, outcomes linked from the sales book and call stats,
/// recurring edits become update proposals, repeated free text becomes
/// "new template?" proposals. Nothing changes a template without the user.
/// </summary>
static class TemplateLearning
{
    public const int MinUsesForStats = 20;
    public const int MinEditRepeats = 3;
    public const int MinClusterSize = 3;
    public const double ClusterSimilarity = 0.5;
    public static readonly TimeSpan OutcomeWindow = TimeSpan.FromDays(14);
    const string NameToken = "{שם}";

    // ---- outcomes ----------------------------------------------------------------

    public static string NormalizeName(string? name) =>
        Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    /// <summary>Same client by name: equal, or the same first two words.</summary>
    public static bool SameClient(string? a, string? b)
    {
        var x = NormalizeName(a);
        var y = NormalizeName(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var xs = x.Split(' ');
        var ys = y.Split(' ');
        return xs.Length >= 2 && ys.Length >= 2 && xs[0] == ys[0] && xs[1] == ys[1];
    }

    /// <summary>Last 9 digits — "+972 54-123-4567" and "054 1234567" match.</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length <= 9 ? digits : digits[^9..];
    }

    public static SendOutcome Outcome(
        TemplateSend send,
        IEnumerable<(string ClientName, DateTime Date)> deals,
        IEnumerable<(DateTime StartedUtc, string? Number)> calls)
    {
        var day = send.AtUtc.ToLocalTime().Date;
        var lastDay = (send.AtUtc + OutcomeWindow).ToLocalTime().Date;
        var deposit = deals.Any(d => d.Date.Date >= day && d.Date.Date <= lastDay && SameClient(d.ClientName, send.Name));
        var key = PhoneKey(send.Phone);
        var nextCall = key.Length >= 7 && calls.Any(c =>
            c.StartedUtc > send.AtUtc && c.StartedUtc <= send.AtUtc + OutcomeWindow && PhoneKey(c.Number) == key);
        return new SendOutcome(deposit, nextCall);
    }

    /// <summary>Null until the template has at least 20 uses.</summary>
    public static TemplateStats? Stats(
        string templateId,
        IEnumerable<TemplateSend> sends,
        IEnumerable<(string ClientName, DateTime Date)> deals,
        IEnumerable<(DateTime StartedUtc, string? Number)> calls)
    {
        var mine = sends.Where(s => s.TemplateId == templateId).ToList();
        if (mine.Count < MinUsesForStats) return null;
        var dealList = deals.ToList();
        var callList = calls.ToList();
        int deposits = 0, nextCalls = 0;
        foreach (var s in mine)
        {
            var o = Outcome(s, dealList, callList);
            if (o.Deposit) deposits++;
            if (o.NextCall) nextCalls++;
        }
        return new TemplateStats(mine.Count, deposits, nextCalls);
    }

    // ---- edit mining -----------------------------------------------------------

    /// <summary>One change: after the common line <see cref="Anchor"/> (null =
    /// start of text), <see cref="Removed"/> lines became <see cref="Added"/>.</summary>
    public sealed record Hunk(string? Anchor, IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

    static string Tokenize(string text, string? name)
    {
        text = text.Replace("\r\n", "\n");
        var nm = (name ?? "").Trim();
        var first = TemplateFill.FirstName(nm);
        if (nm.Length == 0) return text;
        text = text.Replace(nm, NameToken, StringComparison.Ordinal);
        return first == nm ? text : text.Replace(first, NameToken, StringComparison.Ordinal);
    }

    static string LineKey(string line) =>
        Regex.Replace(line.Replace(NameToken, ""), @"\s+", " ").Trim();

    /// <summary>Line-level LCS diff into hunks. Whitespace-only differences are ignored.</summary>
    public static List<Hunk> Diff(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var ka = a.Select(LineKey).ToArray();
        var kb = b.Select(LineKey).ToArray();
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = ka[i] == kb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<Hunk>();
        string? anchor = null;
        var removed = new List<string>();
        var added = new List<string>();
        void Flush()
        {
            if (removed.Count > 0 || added.Count > 0)
                hunks.Add(new Hunk(anchor, removed.ToList(), added.ToList()));
            removed.Clear();
            added.Clear();
        }
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && ka[x] == kb[y])
            {
                Flush();
                anchor = a[x];
                x++;
                y++;
            }
            else if (y < b.Length && (x >= a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) added.Add(b[y++]);
            else removed.Add(a[x++]);
        }
        Flush();
        // Blank-line-only changes are noise.
        return hunks.Where(h => h.Removed.Concat(h.Added).Any(l => LineKey(l).Length > 0)).ToList();
    }

    /// <summary>The edit a send made to its filled template, name-neutral.</summary>
    public static List<Hunk> EditOf(TemplateSend send) =>
        send.FinalText is null ? new() : Diff(Tokenize(send.FilledText, send.Name), Tokenize(send.FinalText, send.Name));

    /// <summary>Stable hash of an edit (normalized), for dedupe and rejection memory.</summary>
    public static string Signature(IReadOnlyList<Hunk> hunks)
    {
        if (hunks.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var h in hunks)
        {
            sb.Append('@').Append(LineKey(h.Anchor ?? "^")).Append('\n');
            foreach (var r in h.Removed) sb.Append('-').Append(LineKey(r)).Append('\n');
            foreach (var r in h.Added) sb.Append('+').Append(LineKey(r)).Append('\n');
        }
        return Hash(sb.ToString());
    }

    /// <summary>Applies hunks to a template's own text (anchors matched
    /// name-neutrally; an anchor missing from the template — the "היי {שם}!"
    /// greeting Palon prepends — means the start). Null if they don't fit.</summary>
    public static string? Apply(string templateText, IReadOnlyList<Hunk> hunks)
    {
        var lines = templateText.Replace("\r\n", "\n").Split('\n').ToList();
        var pos = 0;
        foreach (var h in hunks)
        {
            if (h.Anchor is { } anchor)
            {
                var key = LineKey(anchor);
                var found = -1;
                for (var i = pos; i < lines.Count; i++)
                    if (LineKey(lines[i]) == key) { found = i; break; }
                if (found < 0)
                {
                    if (!anchor.Contains(NameToken) || pos != 0) return null;
                    found = -1; // greeting line Palon added → start of text
                }
                pos = found + 1;
            }
            for (var i = 0; i < h.Removed.Count; i++)
                if (pos + i >= lines.Count || LineKey(lines[pos + i]) != LineKey(h.Removed[i])) return null;
            lines.RemoveRange(pos, h.Removed.Count);
            var add = h.Added.Select(StripName).ToList();
            lines.InsertRange(pos, add);
            pos += add.Count;
        }
        return string.Join("\n", lines);
    }

    static string StripName(string line) =>
        line.Replace(" " + NameToken, "").Replace(NameToken, "");

    /// <summary>
    /// Update proposals: the same edit (by signature) on the same template
    /// version, 3+ times, not already decided. The proposal shows the
    /// template's current text and the text with the edit applied.
    /// </summary>
    public static List<TemplateProposal> EditProposals(
        IReadOnlyList<MessageTemplate> templates, IEnumerable<TemplateSend> sends, ISet<string> decided)
    {
        var result = new List<TemplateProposal>();
        foreach (var t in templates)
        {
            var groups = sends
                .Where(s => s.TemplateId == t.Id && s.Version == t.Version && s.Edited)
                .Select(s => (Send: s, Hunks: EditOf(s)))
                .Where(x => x.Hunks.Count > 0)
                .GroupBy(x => Signature(x.Hunks));
            foreach (var g in groups)
            {
                var n = g.Count();
                var key = "edit:" + t.Id + ":" + g.Key;
                if (n < MinEditRepeats || decided.Contains(key)) continue;
                if (Apply(t.Text, g.Last().Hunks) is not { } after || after == t.Text) continue;
                result.Add(new TemplateProposal(ProposalKind.Edit, key, t.Id, t.Text, after, n));
            }
        }
        return result;
    }

    // ---- clustering free text ------------------------------------------------------

    static readonly Regex Word = new(@"[\p{L}\p{N}]{2,}", RegexOptions.Compiled);

    public static HashSet<string> Tokens(string text, string? name)
    {
        var first = TemplateFill.FirstName(name).ToLowerInvariant();
        return Word.Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => first.Length == 0 || w != first).ToHashSet();
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>Single-link clusters of free-text sends (Jaccard ≥ 0.5); only
    /// clusters of 3+ are returned, each in send order.</summary>
    public static List<List<TemplateSend>> Clusters(IEnumerable<TemplateSend> sends)
    {
        var free = sends.Where(s => s.TemplateId is null && s.SentText.Trim().Length > 0).OrderBy(s => s.AtUtc).ToList();
        var toks = free.Select(s => Tokens(s.SentText, s.Name)).ToList();
        var parent = Enumerable.Range(0, free.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < free.Count; i++)
            for (var j = i + 1; j < free.Count; j++)
                if (Jaccard(toks[i], toks[j]) >= ClusterSimilarity) parent[Find(j)] = Find(i);
        return Enumerable.Range(0, free.Count).GroupBy(Find)
            .Where(g => g.Count() >= MinClusterSize)
            .Select(g => g.Select(i => free[i]).ToList())
            .ToList();
    }

    /// <summary>"New template?" proposals: the most typical message of each
    /// cluster (highest mean similarity), with the client's name taken out.
    /// Keyed by the cluster's first send so a growing cluster stays one card.</summary>
    public static List<TemplateProposal> NewTemplateProposals(IEnumerable<TemplateSend> sends, ISet<string> decided)
    {
        var result = new List<TemplateProposal>();
        foreach (var cluster in Clusters(sends))
        {
            var key = "new:" + cluster[0].Id;
            if (decided.Contains(key)) continue;
            var toks = cluster.Select(s => Tokens(s.SentText, s.Name)).ToList();
            var best = Enumerable.Range(0, cluster.Count)
                .OrderByDescending(i => Enumerable.Range(0, cluster.Count).Where(j => j != i).Average(j => Jaccard(toks[i], toks[j])))
                .ThenByDescending(i => cluster[i].AtUtc)
                .First();
            var text = RemoveName(cluster[best].SentText, cluster[best].Name);
            result.Add(new TemplateProposal(ProposalKind.NewTemplate, key, null, "", text, cluster.Count));
        }
        return result;
    }

    static string RemoveName(string text, string? name)
    {
        text = text.Replace("\r\n", "\n");
        var first = TemplateFill.FirstName(name);
        if (first.Length == 0) return text.Trim();
        var full = (name ?? "").Trim();
        if (full.Length > 0) text = text.Replace(" " + full, "").Replace(full, "");
        return text.Replace(" " + first, "").Replace(first, "").Trim();
    }

    public static List<TemplateProposal> Proposals(
        IReadOnlyList<MessageTemplate> templates, IReadOnlyCollection<TemplateSend> sends, ISet<string> decided) =>
        EditProposals(templates, sends, decided).Concat(NewTemplateProposals(sends, decided)).ToList();

    // ---- diff view -------------------------------------------------------------------

    public enum LineOp { Same, Removed, Added }

    /// <summary>Line diff for display: every line of both texts, in order.</summary>
    public static List<(LineOp Op, string Text)> DiffLines(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<(LineOp, string)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { result.Add((LineOp.Same, a[x])); x++; y++; }
            else if (y < b.Length && (x >= a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) result.Add((LineOp.Added, b[y++]));
            else result.Add((LineOp.Removed, a[x++]));
        }
        return result;
    }

    static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}

/// <summary>
/// %LOCALAPPDATA%\Palon\template_learning.json — the send log and the keys
/// of proposals the user already accepted or rejected. Local only.
/// </summary>
static class TemplateLearningStore
{
    const int MaxSends = 5000;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string? PathOverride { get; set; }
    static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "template_learning.json");

    public static event Action? Changed;

    internal sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<TemplateSend> Sends { get; set; } = new();
        /// <summary>Proposal key → "accepted" | "rejected".</summary>
        public Dictionary<string, string> Decisions { get; set; } = new();
    }

    internal static Envelope? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Envelope();
            var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new JsonException("empty");
            e.Sends ??= new();
            e.Decisions ??= new();
            e.Sends.RemoveAll(s => s is null);
            return e;
        }
        catch (Exception ex)
        {
            Log.Write($"Template learning load failed: {ex.Message}");
            return null;
        }
    }

    public static List<TemplateSend> Sends() => Read()?.Sends ?? new();

    public static HashSet<string> Decided() => (Read()?.Decisions.Keys ?? Enumerable.Empty<string>()).ToHashSet();

    /// <summary>Logs one copy. <paramref name="finalText"/> only when the user
    /// edited it in Palon; pass null for a straight copy.</summary>
    public static TemplateSend? LogCopy(MessageTemplate? template, string? name, string? phone,
        string filledText, string? finalText, string surface)
    {
        filledText = filledText.Replace("\r\n", "\n");
        finalText = finalText?.Replace("\r\n", "\n");
        if (finalText == filledText) finalText = null;
        var send = new TemplateSend(Guid.NewGuid().ToString("n"), DateTime.UtcNow, template?.Id, template?.Version ?? 0,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            filledText, finalText, surface);
        return Mutate(e =>
        {
            e.Sends.Add(send);
            if (e.Sends.Count > MaxSends) e.Sends.RemoveRange(0, e.Sends.Count - MaxSends);
        }) ? send : null;
    }

    public static bool Decide(string key, bool accepted) => Mutate(e => e.Decisions[key] = accepted ? "accepted" : "rejected");

    public static bool Undecide(string key) => Mutate(e => e.Decisions.Remove(key));

    static bool Mutate(Action<Envelope> change)
    {
        var e = Read();
        if (e is null) return false; // never overwrite an unreadable file
        change(e);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(e, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Template learning save failed: {ex.Message}");
            return false;
        }
        Changed?.Invoke();
        return true;
    }
}
