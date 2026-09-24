using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Salesforce;

/// <summary>One node of CDP's Accessibility.getFullAXTree, trimmed to what Palon uses.</summary>
sealed record AxNode(
    string Id,
    string? ParentId,
    string Role,
    string Name,
    int? BackendId,
    bool Ignored,
    bool Disabled,
    bool Modal,
    IReadOnlyList<string> ChildIds,
    string? Value = null)
{
    public string Ref => BackendId is { } b ? "e" + b : "";
}

/// <summary>
/// Pure: parses the AX tree, finds the active scope (an open modal dialog
/// wins over the page), matches role+name locators, and renders the
/// Playwright-style ref snapshot the recovery prompt shows the model.
/// </summary>
static class AxTree
{
    public static List<AxNode> Parse(JsonElement getFullAXTreeResult)
    {
        var list = new List<AxNode>();
        if (!getFullAXTreeResult.TryGetProperty("nodes", out var nodes)) return list;
        foreach (var n in nodes.EnumerateArray())
        {
            var id = n.GetProperty("nodeId").GetString() ?? "";
            string? parent = n.TryGetProperty("parentId", out var p) ? p.GetString() : null;
            var role = Str(n, "role");
            var name = Str(n, "name");
            var value = Str(n, "value");
            int? backend = n.TryGetProperty("backendDOMNodeId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
            var ignored = n.TryGetProperty("ignored", out var ig) && ig.ValueKind == JsonValueKind.True;
            bool disabled = false, modal = false;
            if (n.TryGetProperty("properties", out var props))
                foreach (var prop in props.EnumerateArray())
                {
                    var pn = prop.GetProperty("name").GetString();
                    var truthy = prop.TryGetProperty("value", out var pv) && pv.TryGetProperty("value", out var pvv)
                                 && pvv.ValueKind == JsonValueKind.True;
                    if (pn == "disabled" && truthy) disabled = true;
                    if (pn == "modal" && truthy) modal = true;
                }
            var children = n.TryGetProperty("childIds", out var c)
                ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
            list.Add(new AxNode(id, parent, role, name, backend, ignored, disabled, modal, children, value.Length > 0 ? value : null));
        }
        return list;
    }

    static string Str(JsonElement n, string prop) =>
        n.TryGetProperty(prop, out var o) && o.TryGetProperty("value", out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    /// <summary>The nodes under the top-most open modal dialog, or all nodes.</summary>
    public static IReadOnlyList<AxNode> Scope(IReadOnlyList<AxNode> all)
    {
        var dialog = all.LastOrDefault(n => !n.Ignored && (n.Role is "dialog" or "alertdialog") && (n.Modal || n.Name.Length > 0));
        return dialog is null ? all : Subtree(all, dialog.Id);
    }

    public static IReadOnlyList<AxNode> Subtree(IReadOnlyList<AxNode> all, string rootId)
    {
        var byId = all.ToDictionary(n => n.Id);
        var result = new List<AxNode>();
        var stack = new Stack<string>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            if (!byId.TryGetValue(stack.Pop(), out var n)) continue;
            result.Add(n);
            for (var i = n.ChildIds.Count - 1; i >= 0; i--) stack.Push(n.ChildIds[i]);
        }
        return result;
    }

    /// <summary>Non-ignored nodes matching a role+name locator.</summary>
    public static List<AxNode> Match(IEnumerable<AxNode> nodes, SfLocator l)
    {
        if (l.Role is null || l.Name is null) return new List<AxNode>();
        return nodes.Where(n => !n.Ignored && n.BackendId is not null
                                && string.Equals(n.Role, l.Role, StringComparison.OrdinalIgnoreCase)
                                && LocatorRanking.NameMatches(l, n.Name)).ToList();
    }

    public static bool AnyNameContains(IEnumerable<AxNode> nodes, string text) =>
        text.Length > 0 && nodes.Any(n => !n.Ignored && (n.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                        || (n.Value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)));

    static readonly HashSet<string> Interesting = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "link", "textbox", "searchbox", "combobox", "listbox", "option", "checkbox", "radio",
        "tab", "menuitem", "switch", "spinbutton", "dialog", "alertdialog", "heading", "alert", "status",
        "tablist", "menu", "grid", "row", "gridcell", "cell", "columnheader", "textarea",
    };

    static readonly HashSet<string> Actionable = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "link", "textbox", "searchbox", "combobox", "option", "checkbox", "radio",
        "tab", "menuitem", "switch", "spinbutton",
    };

    /// <summary>
    /// A compact Playwright ai-mode style snapshot:
    /// <c>- button "Save" [ref=e20]</c>, indented by depth, containers kept
    /// only when named. <paramref name="fresh"/> marks refs new since the last
    /// step with <c>*</c> (browser-use's trick). Truncated at maxChars.
    /// </summary>
    public static string Snapshot(IReadOnlyList<AxNode> scope, int maxChars = 12_000, ISet<int>? fresh = null)
    {
        if (scope.Count == 0) return "";
        var byId = scope.ToDictionary(n => n.Id);
        var root = scope[0];
        var sb = new StringBuilder();
        var truncated = false;
        Walk(root, 0);
        if (truncated) sb.Append("- … (truncated)\n");
        return sb.ToString();

        void Walk(AxNode n, int depth)
        {
            if (truncated) return;
            var show = !n.Ignored && Interesting.Contains(n.Role) && (n.Name.Length > 0 || Actionable.Contains(n.Role));
            if (show)
            {
                var line = new StringBuilder();
                line.Append(' ', depth * 2).Append("- ");
                if (fresh is not null && n.BackendId is { } b && fresh.Contains(b)) line.Append('*');
                line.Append(n.Role);
                if (n.Name.Length > 0) line.Append(" \"").Append(Clip(n.Name, 80)).Append('"');
                if (n.Value is { Length: > 0 } v && n.Role is "textbox" or "combobox" or "searchbox")
                    line.Append(" value=\"").Append(Clip(v, 40)).Append('"');
                if (n.Disabled) line.Append(" [disabled]");
                if (n.BackendId is not null && Actionable.Contains(n.Role)) line.Append(" [ref=").Append(n.Ref).Append(']');
                line.Append('\n');
                if (sb.Length + line.Length > maxChars) { truncated = true; return; }
                sb.Append(line);
            }
            foreach (var c in n.ChildIds)
                if (byId.TryGetValue(c, out var child)) Walk(child, show ? depth + 1 : depth);
        }
    }

    static string Clip(string s, int max)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim().Replace("\"", "'");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>
/// browser-use's ActionLoopDetector, adapted: a rolling window of action
/// hashes with soft nudges at 5/8/12 repeats and page stagnation after 5
/// identical fingerprints. Soft — it adds text to the recovery prompt. The
/// hard stop is the job budget (<see cref="MaxRecoveries"/>, <see cref="MaxActions"/>).
/// </summary>
sealed class LoopDetector
{
    public const int Window = 20;
    public const int MaxRecoveries = 2;
    public const int MaxActions = 60;

    readonly Queue<string> _actions = new();
    readonly Queue<string> _pages = new();
    public int Recoveries { get; private set; }
    public int Actions { get; private set; }

    public static string ActionHash(string method, int? backendId, string? text) =>
        method + ":" + backendId + ":" + LocatorRanking.Fold(text ?? "").ToLowerInvariant();

    public void RecordAction(string hash)
    {
        Actions++;
        _actions.Enqueue(hash);
        while (_actions.Count > Window) _actions.Dequeue();
    }

    public void RecordPage(string fingerprint)
    {
        _pages.Enqueue(fingerprint);
        while (_pages.Count > Window) _pages.Dequeue();
    }

    public int RepeatsOfLast => _actions.Count == 0 ? 0 : _actions.Count(a => a == _actions.Last());

    public bool Stagnant => _pages.Count >= 5 && _pages.Reverse().Take(5).Distinct().Count() == 1;

    /// <summary>A nudge for the model, or null.</summary>
    public string? Nudge()
    {
        var r = RepeatsOfLast;
        var parts = new List<string>();
        if (r >= 12) parts.Add($"The same action was repeated {r} times. It is not working: answer null unless a clearly different element fits.");
        else if (r >= 8) parts.Add($"The same action was repeated {r} times. Choose a different element or answer null.");
        else if (r >= 5) parts.Add($"The same action was repeated {r} times; consider whether it is having any effect.");
        if (Stagnant) parts.Add("The page has not changed over the last 5 steps.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>True if another recovery is allowed (and counts it).</summary>
    public bool TryUseRecovery()
    {
        if (Recoveries >= MaxRecoveries) return false;
        Recoveries++;
        return true;
    }

    public bool OverBudget => Actions >= MaxActions;
}

/// <summary>The one-shot recovery prompt (after Stagehand's act prompt) and its reply parser.</summary>
static class RecoveryPrompt
{
    public const string System =
        "You locate one element on a Salesforce Lightning page for a browser automation step. " +
        "You get the step's intent and an accessibility snapshot where actionable elements carry [ref=eN]. " +
        "Reply with JSON only: {\"ref\":\"eN\"} for the single element that performs the intent, " +
        "or {\"ref\":null} if no element clearly matches. Do not fabricate refs. " +
        "Never choose an element that deletes, merges, converts or removes anything.";

    public static string User(string intent, string method, string snapshot, string? nudge) =>
        $"Intent: {intent}\nAction: {method}\n" + (nudge is null ? "" : $"Note: {nudge}\n") + "Snapshot:\n" + snapshot;

    static readonly Regex RefRx = new("\"ref\"\\s*:\\s*(?:\"(?<r>e\\d+)\"|null)", RegexOptions.CultureInvariant);

    /// <summary>The backend node id the model chose, if it's one the snapshot offered.</summary>
    public static int? Parse(string? reply, string snapshot)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var m = RefRx.Match(reply);
        if (!m.Success || !m.Groups["r"].Success) return null;
        var r = m.Groups["r"].Value;
        if (!snapshot.Contains($"[ref={r}]", StringComparison.Ordinal)) return null; // not offered → fabricated
        return int.Parse(r[1..]);
    }
}
