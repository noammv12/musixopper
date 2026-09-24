using System.Globalization;
using System.Text.RegularExpressions;
using Palon.Memory;
using Palon.Notes;
using Palon.Sales;

namespace Palon.Agentic;

enum CommandKind
{
    Callback,
    Deal,
    Template,
    Remember,
    ClientNote,
    Open,
    ReadScreen,
    Salesforce,
    DraftFollowUp,
    Search,
    SummarizeClient,
    NextSteps,
    Brief,
    Recap,
    Focus,
}

/// <summary>What the fast parser understood. Only the fields of its kind are set.</summary>
sealed record ParsedCommand(CommandKind Kind)
{
    public string? Name { get; init; }
    public DateTime? WhenLocal { get; init; }
    /// <summary>The time phrase fixed an hour (not just a day with the 10:00 default).</summary>
    public bool TimeExplicit { get; init; }
    public decimal? Amount { get; init; }
    public DealSource? Source { get; init; }
    public DealRegion? Region { get; init; }
    public string? Affiliate { get; init; }
    public DateTime? Date { get; init; }
    public string? TemplateQuery { get; init; }
    /// <summary>Free text: the note, the memory line, the search query, the draft instruction.</summary>
    public string? Text { get; init; }
    /// <summary>Open: a page key ("month") or "cmd:&lt;id&gt;" for a saved command, "client" for a client card.</summary>
    public string? Target { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool Receipt { get; init; }
    public bool Off { get; init; }
}

/// <summary>What the parser may look names and targets up in. Pure data.</summary>
sealed record ParseContext(DateTime Now, IReadOnlyList<string> ClientNames, IReadOnlyList<(string Id, string Label)> SavedCommands)
{
    public static ParseContext Empty(DateTime now) => new(now, Array.Empty<string>(), Array.Empty<(string, string)>());
}

/// <summary>
/// The command bar's deterministic fast path, Hebrew and English. Recognizes a
/// fixed set of intents and extracts their slots; anything it is not sure about
/// returns null so the UI hands the text to the agent loop instead. Pure.
/// </summary>
static class CommandParser
{
    const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string NotHe = "(?<![א-ת])";
    const string NotHeAfter = "(?![א-ת])";

    // ---- rituals / plan --------------------------------------------------------------
    static readonly Regex BriefRx = new(@"^(?:בריף|תדריך|תדריך בוקר|בריף בוקר|מה על הפרק(?: היום)?|morning brief|brief|briefing)$", Opt);
    static readonly Regex RecapRx = new(@"^(?:סיכום יום|סיכום היום|סכם(?: לי)? את היום|תסכם(?: לי)? את היום|איך היה היום|recap|day recap|end of day|eod|sum up (?:my|the) day)$", Opt);
    static readonly Regex NextRx = new(@"^(?:מה עכשיו|מה הלאה|מה הבא|מה התוכנית(?: להיום)?|מה התכנית(?: להיום)?|מה לעשות(?: היום| עכשיו)?|מה עליי?(?: לעשות)?(?: היום)?|תוכנית(?: ל)?היום|תכנית(?: ל)?היום|צעדים הבאים|plan(?: my day)?|what'?s next|next steps?|what should i do(?: now| today)?)$", Opt);

    // ---- focus -----------------------------------------------------------------------
    static readonly Regex FocusOffRx = new(@"^(?:בטל|תבטל|סיים|תסיים|צא מ|עצור)\s*(?:את\s+)?(?:מצב\s+)?(?:ה)?(?:פוקוס|ריכוז|שקט)$|^(?:focus off|end focus|stop focus|unfocus)$", Opt);
    static readonly Regex FocusRx = new(@"^(?:מצב\s+)?(?:פוקוס|ריכוז|אל תפריע|שקט|focus|do not disturb|dnd)(?:\s+(?:ל-?|for\s+)?(?<rest>.+))?$", Opt);

    // ---- memory ----------------------------------------------------------------------
    static readonly Regex RememberRx = new(@"^(?:תזכור|זכור|תזכרי|תרשום לעצמך|רשום לעצמך|remember|note to self)\s*(?:that\s+|:\s*|-\s*)?(?<text>.+)$", Opt);
    static readonly Regex ClientNoteRx = new(@"^(?:הערה|פתק|note)\s+(?:ל|על|for|on|about)[- ]?\s*(?<who>[^:\-–]+?)\s*[:\-–]\s*(?<text>.+)$", Opt);

    // ---- search ----------------------------------------------------------------------
    static readonly Regex SearchWhoSaidRx = new(@"^מה\s+(?:אמר|אמרה|אמרו|סיפר|סיפרה|רצה|רצתה|ביקש|ביקשה)\s+(?<who>.+?)\s+(?:על|לגבי|בנוגע ל|בקשר ל)-?\s*(?<q>.+)$", Opt);
    static readonly Regex SearchWhoSaid2Rx = new(@"^מה\s+(?<who>.+?)\s+(?:אמר|אמרה|סיפר|סיפרה|רצה|רצתה|ביקש|ביקשה)\s+(?:על|לגבי|בנוגע ל|בקשר ל)-?\s*(?<q>.+)$", Opt);
    static readonly Regex SearchEnRx = new(@"^what did (?<who>.+?) (?:say|tell me|mention|ask) about (?<q>.+)$", Opt);
    static readonly Regex SearchRx = new(@"^(?:חפש|תחפש|מצא|תמצא|search|find)\s+(?:בהערות\s+|בשיחות\s+|in (?:the )?notes\s+|for\s+)?(?<q>.+)$", Opt);
    static readonly Regex WhoMentionedRx = new(@"^(?:מי|who)\s+(?:דיבר|אמר|ביקש|שאל|הזכיר|mentioned|asked|talked)\s+(?:על|לגבי|about)?\s*(?<q>.+)$", Opt);

    // ---- draft -----------------------------------------------------------------------
    static readonly Regex DraftRx = new(
        @"^(?:נסח|תנסח|תנסחי|כתוב|תכתוב|הכן|תכין|draft|write)\s+(?:לי\s+)?(?:הודעת המשך|הודעה|פולואפ|פולו-?אפ|מעקב|follow[- ]?up(?:\s+message)?|message|וואטסאפ|ווטסאפ|whatsapp)\s*(?<rest>.*)$", Opt);
    static readonly Regex FollowUpRx = new(@"^(?:follow[- ]?up|פולואפ|פולו-?אפ|הודעת המשך)\s+(?<rest>.+)$", Opt);

    // ---- template --------------------------------------------------------------------
    static readonly Regex TemplateWordRx = new(NotHe + @"(?:ה)?(?:תבנית|תבניות)" + NotHeAfter + @"|\btemplate\b", Opt);

    // ---- salesforce ------------------------------------------------------------------
    static readonly Regex SalesforceRx = new(@"סיילספורס|סיילס פורס|salesforce|\bsf\b", Opt);
    static readonly Regex SalesforceVerbRx = new(@"^(?:תעד|תתעד|רשום|תרשום|עדכן|תעדכן|log|update|תעלה|העלה)\b|^(?:סיילספורס|salesforce)$", Opt);

    // ---- screen ----------------------------------------------------------------------
    static readonly Regex ReceiptRx = new(@"^(?:קרא|תקרא|סרוק|תסרוק|קח|תקח|read|scan)\s+(?:את\s+)?(?:ה)?(?:פרטים מה)?(?:קבלה|אסמכתא|receipt)|^(?:קבלה|receipt)$", Opt);
    static readonly Regex ScreenRx = new(@"^(?:קרא|תקרא|סכם|תסכם|תראה|הסתכל על|read|summarize|look at)\s+(?:לי\s+)?(?:את\s+)?(?:ה)?(?:מסך|screen|my screen)|^מה\s+(?:כתוב|יש|רואים)\s+(?:ב|על\s+ה)?מסך|^what'?s on (?:my|the) screen|^(?:קרא מסך|read screen)$", Opt);

    // ---- client summary --------------------------------------------------------------
    static readonly Regex SummarizeRx = new(@"^(?:סכם|תסכם|סיכום|summarize|summary of|מי זה|מי זאת|who is|tell me about|ספר לי על|תספר לי על|כרטיס(?: של)?|מה עם|מה המצב עם|what about)\s+(?:את\s+)?(?:ה)?(?:לקוח\s+|לקוחה\s+)?(?<who>.+)$", Opt);

    // ---- open ------------------------------------------------------------------------
    static readonly Regex OpenRx = new(@"^(?:פתח|תפתח|פתחי|open|הצג|תציג|show|go to|לך ל|קפוץ ל)[- ]?\s*(?:לי\s+)?(?:את\s+)?(?<what>.+)$", Opt);

    static readonly (string Key, string[] Words)[] Pages =
    {
        ("today", new[] { "היום", "היום שלי", "עכשיו", "בית", "ראשי", "today", "now", "home" }),
        ("callbacks", new[] { "חזרות", "תזכורות", "יומן", "callbacks", "reminders" }),
        ("month", new[] { "חודש", "החודש", "מכירות", "עסקאות", "הפקדות", "יעד", "month", "sales", "deals", "target" }),
        ("clients", new[] { "לקוחות", "clients", "customers" }),
        ("templates", new[] { "תבניות", "templates" }),
        ("coaching", new[] { "אימון", "קואצינג", "קואצ'ינג", "coaching", "coach" }),
        ("calls", new[] { "שיחות", "השיחות", "שיחות אחרונות", "calls" }),
        ("memory", new[] { "זיכרון", "זכרון", "memory" }),
        ("settings", new[] { "הגדרות", "settings", "preferences" }),
    };

    // ---- deal ------------------------------------------------------------------------
    static readonly Regex DealVerbRx = new(NotHe + @"(?:ה)?(?:הפקיד|הפקידה|הפקידו|הפקדה|הפקדת|מפקיד|מפקידה|עסקה|סגר|סגרה|סגרתי)" + NotHeAfter + @"|\b(?:deposited|deposit|ftd|deal|closed)\b", Opt);
    static readonly Regex AmountRx = new(@"(?<![\d:.])\$?\s?(?<n>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s?(?<k>k|K|אלף|א׳|א')?(?![\d:])\s?\$?", Opt);
    static readonly Regex AffiliateRx = new(NotHe + @"(?:דרך|מ-?אפילייט|via|aff(?:iliate)?\s*[:=])\s*(?<a>[A-Za-z][A-Za-z0-9_.-]*(?:\s+[A-Z][A-Za-z0-9_.-]*)?)", RegexOptions.CultureInvariant);

    // ---- callback --------------------------------------------------------------------
    static readonly Regex CallbackVerbRx = new(
        @"^(?:תזכיר(?:י)?\s+לי\s+|להזכיר\s+לי\s+|תזכיר(?:י)?\s+|תקבע(?:\s+לי)?\s+(?:חזרה\s+)?|קבע(?:\s+לי)?\s+(?:חזרה\s+)?|חזרה\s+|לחזור\s+|תחזור\s+|להתקשר\s+|תתקשר\s+|התקשר\s+|צלצל\s+|remind me to\s+|remind me\s+|call back\s+|callback\s+|call\s+|ring\s+|phone\s+|to\s+)+",
        Opt);
    static readonly Regex[] TimeStrip =
    {
        new(NotHe + @"(?:ב)?(?:מחרתיים|מחר|היום|הערב|בערב|הבוקר|בבוקר|בצהריים|צהריים|אחר הצהריים|אחה""צ|אחה״צ)" + NotHeAfter, Opt),
        new(NotHe + @"(?:ב)?שבוע הבא" + NotHeAfter, Opt),
        new(NotHe + @"(?:ב)?עוד\s+(?:\d+\s*)?(?:רבע\s+)?(?:שעתיים|שעה|שעות|דקות|דק׳|דק|חצי שעה)" + NotHeAfter, Opt),
        new(NotHe + @"חצי שעה" + NotHeAfter, Opt),
        new(NotHe + @"(?:ב)?יום\s+(?:ראשון|שני|שלישי|רביעי|חמישי|שישי)" + NotHeAfter, Opt),
        new(NotHe + @"(?:ב)?שבת" + NotHeAfter, Opt),
        new(NotHe + @"(?:בשעה\s*|ב-?\s?)\d{1,2}(?::\d{2})?(?:\s*(?:am|pm))?(?![\d:])", Opt),
        new(@"(?<![\d:])\d{1,2}:\d{2}(?:\s*(?:am|pm))?", Opt),
        new(@"(?<![\d:])\d{1,2}\s*(?:am|pm)\b", Opt),
        new(NotHe + @"(?:ב|בשעה\s)-?(?:אחת עשרה|אחת-עשרה|שתים עשרה|שתיים עשרה|אחת|שתיים|שלוש|ארבע|חמש|שש|שבע|שמונה|תשע|עשר)(?:\s+וחצי)?" + NotHeAfter, Opt),
        new(@"\b(?:the\s+)?day after tomorrow\b|\btomorrow\b|\btoday\b|\btonight\b|\bthis (?:morning|afternoon|evening)\b|\bin the (?:morning|afternoon|evening)\b|\b(?:morning|afternoon|evening|noon)\b|\bnext week\b", Opt),
        new(@"\b(?:on\s+)?(?:mon|tues|wednes|thurs|fri|satur|sun)day\b", Opt),
        new(@"\bat\s+\d{1,2}(?::\d{2})?\s*(?:am|pm)?\b|@\s*\d{1,2}(?::\d{2})?", Opt),
        new(@"\bin\s+(?:an?|one|two|three|half an?|\d+)\s*(?:hours?|hrs?|minutes?|mins?)\b", Opt),
    };
    static readonly Regex NoteLeadRx = new(@"^(?:על|לגבי|בנוגע ל|בקשר ל|בעניין|about|re|regarding|to)\b[:\s-]*|^[:\-–·]+\s*", Opt);
    static readonly Regex QuestionRx = new(@"^(?:מה|מתי|למה|איך|כמה|האם|מי|איפה|what|when|why|how|who|where|is|are|do|does)\b|\?$", Opt);

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "את", "אל", "עם", "של", "לי", "גם", "and", "with", "the", "to", "a", "an", "for", "me", "please", "בבקשה",
    };

    /// <summary>Parses one command line; null = not a fast-path command (ask the agent).</summary>
    public static ParsedCommand? Parse(string? input, ParseContext ctx)
    {
        var text = Clean(input);
        if (text.Length < 2) return null;

        if (BriefRx.IsMatch(text)) return new(CommandKind.Brief);
        if (RecapRx.IsMatch(text)) return new(CommandKind.Recap);
        if (NextRx.IsMatch(text)) return new(CommandKind.NextSteps);
        if (FocusOffRx.IsMatch(text)) return new(CommandKind.Focus) { Off = true };
        if (FocusRx.Match(text) is { Success: true } fm)
        {
            var rest = fm.Groups["rest"].Success ? fm.Groups["rest"].Value : "";
            var dur = rest.Length == 0 ? TimeSpan.FromHours(1) : ParseDuration(rest);
            if (dur is null) return null;
            return new(CommandKind.Focus) { Duration = dur };
        }

        if (RememberRx.Match(text) is { Success: true } rm) return Remember(rm.Groups["text"].Value, ctx);
        if (ClientNoteRx.Match(text) is { Success: true } cn)
        {
            var who = ResolveName(cn.Groups["who"].Value.Trim(), ctx, stripL: false);
            var body = cn.Groups["text"].Value.Trim();
            return who is null || body.Length == 0 ? null : new(CommandKind.ClientNote) { Name = who, Text = body };
        }

        if (SearchWhoSaidRx.Match(text) is { Success: true } s1) return Search(s1.Groups["q"].Value, s1.Groups["who"].Value, ctx);
        if (SearchWhoSaid2Rx.Match(text) is { Success: true } s2) return Search(s2.Groups["q"].Value, s2.Groups["who"].Value, ctx);
        if (SearchEnRx.Match(text) is { Success: true } s3) return Search(s3.Groups["q"].Value, s3.Groups["who"].Value, ctx);
        if (WhoMentionedRx.Match(text) is { Success: true } s4) return Search(s4.Groups["q"].Value, null, ctx);
        if (SearchRx.Match(text) is { Success: true } s5)
        {
            var q = s5.Groups["q"].Value.Trim();
            var who = FindKnownName(q, ctx.ClientNames);
            return Search(who is null ? q : RemoveName(q, who), who, ctx);
        }

        if (DraftRx.Match(text) is { Success: true } dm) return Draft(dm.Groups["rest"].Value, ctx);
        if (FollowUpRx.Match(text) is { Success: true } fu) return Draft(fu.Groups["rest"].Value, ctx);

        if (TemplateWordRx.IsMatch(text) && Template(text, ctx) is { } tpl) return tpl;

        if (SalesforceRx.IsMatch(text) && (SalesforceVerbRx.IsMatch(text) || text.Split(' ').Length <= 4))
        {
            var who = FindKnownName(text, ctx.ClientNames) ?? NameAround(text, @"(?:עם|של|with|for)\s+");
            return new(CommandKind.Salesforce) { Name = who };
        }

        if (ReceiptRx.IsMatch(text)) return new(CommandKind.ReadScreen) { Receipt = true };
        if (ScreenRx.IsMatch(text)) return new(CommandKind.ReadScreen) { Text = text };

        if (SummarizeRx.Match(text) is { Success: true } sm)
        {
            var raw = sm.Groups["who"].Value.Trim();
            var who = FindKnownName(raw, ctx.ClientNames);
            if (who is not null && HebrewText.Normalize(RemoveName(raw, who)).Length <= 3) return new(CommandKind.SummarizeClient) { Name = who };
        }

        if (OpenRx.Match(text) is { Success: true } om && Open(om.Groups["what"].Value, ctx) is { } open) return open;

        if (DealVerbRx.IsMatch(text) && Deal(text, ctx) is { } deal) return deal;

        return Callback(text, ctx);
    }

    // ---- intents ---------------------------------------------------------------------

    static ParsedCommand? Remember(string raw, ParseContext ctx)
    {
        var text = raw.Trim();
        // "תזכור שדני …" → "דני …"; but keep a name that itself starts with ש ("שרית").
        if (text.Length > 2 && text[0] == 'ש')
        {
            var first = text.Split(' ')[0];
            var known = ctx.ClientNames.Any(n => HebrewText.Normalize(n).Split(' ')[0] == HebrewText.Normalize(first));
            if (!known) text = text[1..].TrimStart('-', ' ');
        }
        if (text.StartsWith("that ", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        text = text.Trim();
        return text.Length < 2 ? null : new(CommandKind.Remember) { Text = text };
    }

    static ParsedCommand? Search(string query, string? who, ParseContext ctx)
    {
        var q = query.Trim().TrimEnd('?', '.', '!').Trim();
        var name = who is null ? null : ResolveName(who.Trim(), ctx, stripL: false);
        if (q.Length == 0 && name is null) return null;
        return new(CommandKind.Search) { Text = q, Name = name };
    }

    static ParsedCommand? Draft(string rest, ParseContext ctx)
    {
        var r = rest.Trim();
        r = Regex.Replace(r, @"^(?:ל|אל|to|for|with)[- ]?\s*", "", Opt).Trim();
        // A known name anywhere wins; else the first word (the ל was stripped above).
        var who = FindKnownName(r, ctx.ClientNames);
        string instruction;
        if (who is not null) instruction = RemoveName(r, who);
        else
        {
            var words = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new(CommandKind.DraftFollowUp);
            who = words[0];
            instruction = string.Join(' ', words.Skip(1));
        }
        instruction = NoteLeadRx.Replace(instruction.Trim(), "").Trim();
        return new(CommandKind.DraftFollowUp) { Name = who, Text = instruction.Length > 0 ? instruction : null };
    }

    static ParsedCommand? Template(string text, ParseContext ctx)
    {
        // Drop the verbs and the word "template" itself; what's left is "<query> ל<name>" in any order.
        var t = Regex.Replace(text, @"^(?:שלח|תשלח|העתק|תעתיק|תן|תביא|copy|send|give me|paste)\s+(?:לי\s+)?(?:את\s+)?", "", Opt);
        t = TemplateWordRx.Replace(t, " ");
        var who = FindKnownName(t, ctx.ClientNames);
        string rest;
        if (who is not null) rest = RemoveName(t, who);
        else
        {
            var m = Regex.Match(t, NotHe + @"(?:ל|אל|for|to)[- ]?\s*(?<n>[א-תA-Za-z][א-תA-Za-z'׳-]+)\s*$", Opt);
            if (m.Success)
            {
                who = m.Groups["n"].Value;
                rest = t[..m.Index];
            }
            else rest = t;
        }
        rest = Regex.Replace(rest, @"(?<![א-ת])(?:ל|אל|for|to|את|של)(?![א-ת])", " ", Opt);
        var query = Collapse(rest).Trim('-', ' ', ':');
        if (query.Length == 0) return null;
        return new(CommandKind.Template) { TemplateQuery = query, Name = who };
    }

    static ParsedCommand? Open(string what, ParseContext ctx)
    {
        var w = Collapse(what).Trim();
        var norm = HebrewText.Normalize(w);
        foreach (var (key, words) in Pages)
            if (words.Any(x => HebrewText.Normalize(x) == norm || "ה" + HebrewText.Normalize(x) == norm || "את " + HebrewText.Normalize(x) == norm))
                return new(CommandKind.Open) { Target = key };
        foreach (var (id, label) in ctx.SavedCommands)
        {
            var l = HebrewText.Normalize(label);
            if (l.Length > 0 && (l == norm || l == StripHe(norm) || (norm.Length >= 3 && l.StartsWith(norm, StringComparison.Ordinal))))
                return new(CommandKind.Open) { Target = "cmd:" + id, Text = label };
        }
        if (FindKnownName(w, ctx.ClientNames) is { } who && HebrewText.Normalize(RemoveName(w, who)).Replace("כרטיס", "").Replace("של", "").Trim().Length == 0)
            return new(CommandKind.Open) { Target = "client", Name = who };
        return null;
    }

    static ParsedCommand? Deal(string text, ParseContext ctx)
    {
        decimal? amount = null;
        Match? amountMatch = null;
        foreach (Match m in AmountRx.Matches(text))
        {
            if (!decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) continue;
            if (m.Groups["k"].Success) n *= 1000;
            if (n < 10) continue; // a stray digit, not a deposit
            amount = n;
            amountMatch = m;
            break;
        }
        if (amount is null) return null;

        var lower = text.ToLowerInvariant();
        DealSource? source = null;
        if (Regex.IsMatch(lower, @"\bppc\b")) source = DealSource.PPC;
        else if (Regex.IsMatch(lower, @"\b(?:referral|rff|raf)\b|חבר מביא חבר")) source = DealSource.Referral;
        else if (Regex.IsMatch(lower, @"\borganic\b|אורגני")) source = DealSource.Organic;
        else if (Regex.IsMatch(lower, @"\baffiliate\b|אפילייט|אפיליאייט|שותף")) source = DealSource.Affiliate;

        DealRegion? region = null;
        if (Regex.IsMatch(lower, NotHe + @"(?:ב)?ישראל" + NotHeAfter + @"|\bisrael\b|\bil\b")) region = DealRegion.Israel;
        else if (Regex.IsMatch(lower, NotHe + @"(?:ב)?פרו" + NotHeAfter + @"|\bpro\b")) region = DealRegion.Pro;

        var date = ctx.Now.Date;
        if (Regex.IsMatch(lower, NotHe + "אתמול" + NotHeAfter + @"|\byesterday\b")) date = date.AddDays(-1);

        string? affiliate = AffiliateRx.Match(text) is { Success: true } am && SalesLabels.ParseSource(am.Groups["a"].Value) is null
            ? am.Groups["a"].Value.Trim() : null;

        // Name: a known client anywhere; else the words before the verb, else after "של/from/deal".
        var who = FindKnownName(text, ctx.ClientNames);
        if (who is null)
        {
            var verb = DealVerbRx.Match(text);
            var before = Collapse(text[..verb.Index]);
            before = Regex.Replace(before, @"^(?:עסקה|deal|ftd|הפקדה)\s*[:\-]?\s*", "", Opt);
            if (amountMatch is not null && amountMatch.Index < verb.Index) before = Collapse(text[..Math.Min(amountMatch.Index, verb.Index)]);
            var candidate = CleanName(before);
            if (candidate is null)
            {
                var after = Regex.Match(text, @"(?:של|from|by|deal|עסקה|הפקדה)\s*[:\-]?\s*(?<n>[א-תA-Za-z][א-תA-Za-z'׳-]*(?:\s+[א-תA-Za-z][א-תA-Za-z'׳-]*)?)", Opt);
                if (after.Success) candidate = CleanName(after.Groups["n"].Value);
            }
            who = candidate;
        }
        if (who is null) return null;
        return new(CommandKind.Deal) { Name = who, Amount = amount, Source = source, Region = region, Date = date, Affiliate = affiliate };
    }

    static ParsedCommand? Callback(string text, ParseContext ctx)
    {
        if (QuestionRx.IsMatch(text)) return null;
        if (!TimePhrase.TryResolve(text, ctx.Now, out var when, out var complete)) return null;
        if (when <= ctx.Now.AddMinutes(-1)) return null;

        var hadVerb = CallbackVerbRx.Match(text) is { Success: true, Length: > 0 };
        var rest = text;
        foreach (var rx in TimeStrip) rest = rx.Replace(rest, " ");
        rest = Collapse(rest);
        var verb = CallbackVerbRx.Match(rest);
        if (verb.Success && verb.Length > 0) { rest = rest[(verb.Index + verb.Length)..]; hadVerb = true; }
        rest = Collapse(rest).Trim('-', ',', '.', ' ');

        string? who = FindKnownName(rest, ctx.ClientNames);
        string note;
        if (who is not null)
        {
            note = RemoveName(rest, who);
        }
        else
        {
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0 && Stop.Contains(words[0])) words.RemoveAt(0);
            if (words.Count == 0)
            {
                // "תזכיר לי מחר ב-11" with nothing else: a reminder without a person is fine only with a verb.
                return hadVerb ? new(CommandKind.Callback) { WhenLocal = when, TimeExplicit = complete, Text = "" } : null;
            }
            var first = words[0];
            var infinitive = Infinitives.Contains(first);
            if (!infinitive && hadVerb && first.Length >= 3 && first[0] == 'ל' && HebrewText.IsHebrew(first[1])) first = first[1..];
            if (infinitive || NoteLeadRx.IsMatch(first + " ") || !LooksLikeName(first))
            {
                if (!hadVerb) return null;
                who = null;
                note = string.Join(' ', words);
            }
            else
            {
                who = first;
                note = string.Join(' ', words.Skip(1));
            }
        }
        note = NoteLeadRx.Replace(Collapse(note), "").Trim(' ', '-', ',', '.');
        // Without a callback verb, a long tail means this probably isn't a callback at all.
        if (!hadVerb && note.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return null;
        return new(CommandKind.Callback) { Name = who, WhenLocal = when, TimeExplicit = complete, Text = note };
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>"שעתיים", "30 דקות", "2h", "45m", "עד 14:00" → a length; null when unreadable.</summary>
    internal static TimeSpan? ParseDuration(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        if (t.Length == 0) return TimeSpan.FromHours(1);
        if (t.Contains("חצי שעה") || t.Contains("half an hour") || t.Contains("half hour")) return TimeSpan.FromMinutes(30);
        if (t.Contains("שעתיים")) return TimeSpan.FromHours(2);
        if (t is "שעה" or "שעה אחת" or "an hour" or "one hour" or "hour") return TimeSpan.FromHours(1);
        var m = Regex.Match(t, @"(\d+(?:\.\d+)?)\s*(שעות|שעה|דקות|דק׳|דק|h|hr|hrs|hours?|m|min|mins|minutes?)");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            var hours = m.Groups[2].Value.StartsWith("ש") || m.Groups[2].Value.StartsWith("h");
            var span = hours ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
            return span <= TimeSpan.FromHours(12) ? span : null;
        }
        return null;
    }

    static string Clean(string? input)
    {
        var t = (input ?? "").Replace('׳', '\'').Replace('״', '"').Replace('’', '\'').Replace('–', '-').Replace('—', '-');
        t = Collapse(t);
        return t.TrimEnd('.', '!', ' ');
    }

    static string Collapse(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    static string StripHe(string s) => s.Length > 2 && "הלבו".Contains(s[0]) ? s[1..] : s;

    /// <summary>The longest known client name whose words all appear in the text (prefix letters allowed).</summary>
    internal static string? FindKnownName(string text, IReadOnlyList<string> names)
    {
        var tokens = HebrewText.Tokens(text);
        string? best = null;
        var bestLen = 0;
        foreach (var name in names)
        {
            var parts = HebrewText.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => p.Length < 2)) continue;
            if (!parts.All(tokens.Contains)) continue;
            var len = parts.Sum(p => p.Length) + parts.Length;
            if (len > bestLen) { best = name; bestLen = len; }
        }
        // Also accept the first name alone when the text says only "דני" and we know "דני כהן".
        if (best is null)
        {
            foreach (var name in names)
            {
                var first = HebrewText.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is { Length: >= 2 } && tokens.Contains(first)) return name;
            }
        }
        return best;
    }

    /// <summary>Removes a name's words (with any one-letter prefix) from text.</summary>
    internal static string RemoveName(string text, string name)
    {
        var parts = HebrewText.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w =>
            {
                var n = HebrewText.Normalize(w);
                if (parts.Contains(n)) return false;
                if (n.Length > 2 && "לשוהבמכ".Contains(n[0]) && parts.Contains(n[1..])) return false;
                return true;
            });
        return Collapse(string.Join(' ', words));
    }

    static string? ResolveName(string raw, ParseContext ctx, bool stripL)
    {
        var r = Collapse(raw).Trim('?', '.', ',', ' ');
        if (r.Length == 0) return null;
        if (FindKnownName(r, ctx.ClientNames) is { } known) return known;
        if (stripL && r.Length > 2 && r[0] == 'ל') r = r[1..];
        return CleanName(r);
    }

    static string? NameAround(string text, string leadPattern)
    {
        var m = Regex.Match(text, leadPattern + @"(?<n>[א-תA-Za-z][א-תA-Za-z'׳-]*)", Opt);
        return m.Success ? CleanName(m.Groups["n"].Value) : null;
    }

    /// <summary>One or two name-looking words, else null.</summary>
    static string? CleanName(string raw)
    {
        var words = Collapse(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Stop.Contains(w)).ToList();
        if (words.Count is 0 or > 2) return null;
        if (!words.All(LooksLikeName)) return null;
        return string.Join(' ', words);
    }

    static readonly HashSet<string> Infinitives = new(StringComparer.Ordinal)
    {
        "לשלוח", "לבדוק", "לדבר", "לעדכן", "לסגור", "לקבוע", "לחזור", "להתקשר", "לשאול", "להזכיר", "לתאם", "לברר",
        "להעביר", "לפתוח", "לסיים", "לענות", "לנסות", "לוודא", "להכין", "לכתוב", "לטפל", "לגבות", "להציע",
    };

    static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "הוא", "היא", "הם", "אני", "אתה", "זה", "זאת", "הלקוח", "הלקוחה", "לקוח", "לקוחה", "כולם", "מישהו", "עוד", "גם", "רק", "כבר",
        "he", "she", "they", "someone", "client", "everyone", "him", "her", "them", "it",
    };

    static bool LooksLikeName(string w) =>
        w.Length >= 2 && !NotNames.Contains(w) && w.All(c => char.IsLetter(c) || c is '\'' or '-' or '׳');
}
