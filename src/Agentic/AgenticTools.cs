using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Palon.Agent;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.Agentic;

/// <summary>A deal Palon read out of a call note, for the user to confirm.</summary>
sealed record DealDraft(string? ClientName, decimal? Amount, DealSource? Source, DealRegion? Region, string? Evidence, string? NoteId)
{
    public bool Complete => ClientName is not null && Amount is > 0;
}

/// <summary>Reads deposit details out of a call note. Pure, deterministic.</summary>
static class DealFromNote
{
    static readonly Regex DepositWord = new(@"הפקיד|הפקידה|הפקדה|להפקיד|יפקיד|מפקיד|העביר|העבירה|העברה|deposit|transfer|wired|ftd", RegexOptions.IgnoreCase);
    static readonly Regex Amount = new(@"(?<![\d:.])(?<cur>\$|₪|usd|dollars?|דולר)?\s?(?<n>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s?(?<k>k|אלף|א׳)?\s?(?<cur2>\$|₪|usd|dollars?|דולר|שקל(?:ים)?|ש""ח)?(?![\d:])", RegexOptions.IgnoreCase);

    public static DealDraft Extract(CallNote note, string? clientName)
    {
        var text = ((note.Summary ?? "") + "\n" + note.Transcript).Replace("\r\n", "\n");
        decimal? amount = null;
        string? evidence = null;
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?\n])\s*"))
        {
            if (!DepositWord.IsMatch(sentence)) continue;
            foreach (Match m in Amount.Matches(sentence))
            {
                if (!decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) continue;
                if (m.Groups["k"].Success) n *= 1000;
                var hasCurrency = m.Groups["cur"].Success || m.Groups["cur2"].Success || m.Groups["k"].Success;
                if (n < 100 || (!hasCurrency && n < 250)) continue; // hours, days, small counts
                amount = n;
                evidence = sentence.Trim();
                break;
            }
            if (amount is not null) break;
        }
        var lower = text.ToLowerInvariant();
        DealSource? source = Regex.IsMatch(lower, @"\bppc\b|גוגל|google") ? DealSource.PPC
            : Regex.IsMatch(lower, "חבר מביא חבר|referral|הפנה אותו|המליץ") ? DealSource.Referral
            : Regex.IsMatch(lower, @"\baffiliate\b|אפילייט") ? DealSource.Affiliate
            : null;
        DealRegion? region = Regex.IsMatch(lower, "ישראל|פיקוח ישראלי|israel") ? DealRegion.Israel
            : Regex.IsMatch(lower, @"(?<![א-ת])פרו(?![א-ת])|\bpro\b") ? DealRegion.Pro
            : null;
        return new DealDraft(clientName, amount, source, region, evidence is null ? null : (evidence.Length > 160 ? evidence[..160] + "…" : evidence), note.Id);
    }
}

sealed class DraftFollowUpTool : AgentTool
{
    public override string Name => "draft_followup";
    public override string Description =>
        "Draft a personalized WhatsApp follow-up for a client from their latest call note and what Palon remembers " +
        "about them (re-engagement wording when they went quiet). The draft is copied to the clipboard. Use when the " +
        "user asks to write/draft a message or follow-up to a client.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Client name as the user said it."},
          "phone":{"type":"string","description":"Client phone, if known."},
          "instruction":{"type":"string","description":"Optional extra wish, e.g. 'mention the minimum deposit' or 'shorter'."}
        }}
        """;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(60);
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => "מנסח הודעה";

    public override async Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var card = s.FindClient(Str(args, "phone")) ?? s.FindClient(Str(args, "name"));
        if (card is null) return new ToolOutcome("No client by that name/phone in the call notes or callbacks. Ask the user who they mean, or draft without context.");
        var (text, error) = await FollowUpDrafts.GetOrCreateAsync(s, card, Str(args, "instruction"), ct);
        if (text is null) return new ToolOutcome("Drafting failed: " + error);
        var copied = AgenticHost.CopyText(text);
        return new ToolOutcome($"Draft for {card.Name}{(copied ? " (already copied to the clipboard — say so)" : "")}:\n{text}\n\nShow the draft to the user as is.");
    }
}

sealed class SummarizeClientTool : AgentTool
{
    public override string Name => "summarize_client";
    public override string Description =>
        "Everything Palon knows about one client, locally: status, calls, last contact, remembered facts, buying signals, " +
        "objections, the next callback, deposits and a suggested next move. Use for 'who is X', 'what's with X', 'summarize X'.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Client name."},
          "phone":{"type":"string","description":"Client phone."}
        }}
        """;
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => "מסכם את הלקוח";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var card = s.FindClient(Str(args, "phone")) ?? s.FindClient(Str(args, "name"));
        return Task.FromResult(card is null
            ? new ToolOutcome("No client by that name/phone. Try find_in_notes or recall_client.")
            : new ToolOutcome(ClientSummaries.Build(s, card).ToText()));
    }
}

sealed class NextStepsTodayTool : AgentTool
{
    public override string Name => "next_steps_today";
    public override string Description =>
        "Today's concrete plan, built from the user's own promises (callbacks due/overdue), callback promises heard on calls " +
        "and not yet accepted, agreed next steps with nothing booked, fresh buying signals and warm leads that went quiet. " +
        "Use for 'what now', 'what's my plan', 'what should I do'. Present it as is — it is not a generic call list.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => "בונה תוכנית להיום";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var plan = NextSteps.Plan(s);
        var pace = Rituals.PaceLine(s, morning: s.Now.Hour < 12);
        var text = plan.Count == 0 ? "Nothing open: no callbacks due, no pending promises, no warm leads waiting." : string.Join("\n", plan.Select(p => "- " + p.ToLine(s.Now)));
        return Task.FromResult(new ToolOutcome(text + (pace is null ? "" : "\nPace: " + pace)));
    }
}

sealed class RitualTool : AgentTool
{
    public override string Name => "daily_brief";
    public override string Description =>
        "The morning brief (today's callbacks, target pace, opportunities, coaching tip) or the end-of-day recap " +
        "(calls, deposits, bonus, what's still open, tomorrow's callbacks) — computed locally.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "kind":{"type":"string","enum":["morning","end_of_day"],"description":"morning brief or end-of-day recap."}
        },"required":["kind"]}
        """;
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => Str(args, "kind") == "end_of_day" ? "מסכם את היום" : "מכין בריף";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var text = Str(args, "kind") == "end_of_day"
            ? Rituals.Recap(s).ToText(s.Now)
            : Rituals.Brief(s, Settings.RepName, NudgeRules.TipOfTheDay(s.Now)).ToText(s.Now);
        return Task.FromResult(new ToolOutcome(text));
    }
}

sealed class PrepareDealFromNoteTool : AgentTool
{
    public override string Name => "prepare_deal_from_note";
    public override string Description =>
        "Read deposit details (amount, source, region) out of a client's latest call note to prepare a deal. Returns the " +
        "proposed fields and the sentence they came from. Confirm with the user, then save with add_deal.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Client name."},
          "phone":{"type":"string","description":"Client phone."}
        }}
        """;
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => "קורא את פרטי ההפקדה מהשיחה";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var card = s.FindClient(Str(args, "phone")) ?? s.FindClient(Str(args, "name"));
        var note = card is not null ? s.LatestNote(card) : s.Notes.OrderByDescending(n => n.StartedUtc).FirstOrDefault();
        if (note is null) return Task.FromResult(new ToolOutcome("No call note to read from."));
        var name = card?.Name ?? Str(args, "name") ?? s.Who(note.Number);
        var d = DealFromNote.Extract(note, name);
        var previous = card?.Deals.OrderByDescending(x => x.Date).FirstOrDefault();
        var sb = new StringBuilder($"From the call {He.Ago(note.StartedUtc.ToLocalTime(), s.Now)} with {name}:");
        sb.Append($"\n- amount: {(d.Amount is { } a ? "$" + He.N(a) : "not found in the note — ask the user")}");
        sb.Append($"\n- source: {(d.Source ?? previous?.Source)?.ToString() ?? "unknown (default Affiliate)"}");
        sb.Append($"\n- region: {(d.Region ?? previous?.Region)?.ToString() ?? "unknown (default Pro)"}");
        if (d.Evidence is not null) sb.Append($"\n- evidence: \"{d.Evidence}\"");
        sb.Append("\nConfirm the values with the user before calling add_deal.");
        return Task.FromResult(new ToolOutcome(sb.ToString()));
    }
}

sealed class AddDealTool : AgentTool
{
    public override string Name => "add_deal";
    public override string Description =>
        "Record a first-time deposit (FTD) in the month's sales book. Only after the user stated or confirmed the amount. Undoable.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "client_name":{"type":"string"},
          "amount":{"type":"number","description":"Deposit in USD."},
          "source":{"type":"string","enum":["Affiliate","PPC","Organic","Referral"]},
          "region":{"type":"string","enum":["Pro","Israel"]},
          "affiliate":{"type":"string"},
          "date":{"type":"string","description":"yyyy-MM-dd, default today."}
        },"required":["client_name","amount"]}
        """;
    public override ToolRisk Risk => ToolRisk.Reversible;
    public override string ProgressLabel(JsonElement args) => "רושם הפקדה";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "client_name")?.Trim();
        decimal amount = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("amount", out var av))
        {
            if (av.ValueKind == JsonValueKind.Number) av.TryGetDecimal(out amount);
            else if (av.ValueKind == JsonValueKind.String) decimal.TryParse(av.GetString()?.Replace(",", "").Replace("$", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
        }
        if (string.IsNullOrEmpty(name) || amount <= 0) return Task.FromResult(new ToolOutcome("client_name and a positive amount are required."));
        var date = DateTime.TryParseExact(Str(args, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.Date : DateTime.Now.Date;
        if (date > DateTime.Now.Date) return Task.FromResult(new ToolOutcome("The date is in the future."));
        var deal = new Deal
        {
            ClientName = name,
            Amount = amount,
            Date = date,
            Source = Enum.TryParse<DealSource>(Str(args, "source"), true, out var src) ? src : DealSource.Affiliate,
            Region = Enum.TryParse<DealRegion>(Str(args, "region"), true, out var reg) ? reg : DealRegion.Pro,
            Affiliate = Str(args, "affiliate"),
            CreatedFrom = DealOrigin.Manual,
        };
        var book = SalesStore.Open(date.Year, date.Month);
        book.Deals.Add(deal);
        if (!SalesStore.SaveMonth(book)) return Task.FromResult(new ToolOutcome("Saving failed — the sales file is unreadable."));
        var bonus = SalesStore.LoadRules().FtdBonus(deal);
        var progress = book.Target is int t && t > 0 ? $"{book.Deals.Count}/{t}" : $"{book.Deals.Count}";
        return Task.FromResult(new ToolOutcome($"Saved: {name} ${He.N(amount)} ({deal.Source}, {deal.Region}), FTD bonus ₪{bonus}. Month: {progress}.")
        {
            Undo = () =>
            {
                var m = SalesStore.Open(date.Year, date.Month);
                m.Deals.RemoveAll(x => x.Id == deal.Id);
                SalesStore.SaveMonth(m);
                return Task.FromResult("ההפקדה הוסרה");
            },
        });
    }
}

sealed class FindInNotesTool : AgentTool
{
    public override string Name => "find_in_notes";
    public override string Description =>
        "Search ALL call notes (including older archived days) with Hebrew-aware matching, optionally for one client " +
        "(by name or phone) and a time window. Returns the best snippets, newest first on ties, plus remembered facts " +
        "that match. Prefer this over search_notes for 'what did X say about Y' and 'who mentioned Z'.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"What to look for (words in any form; empty = latest notes)."},
          "name":{"type":"string","description":"Only this client's calls."},
          "phone":{"type":"string","description":"Only this number's calls."},
          "since_days":{"type":"integer","description":"Only the last N days."},
          "limit":{"type":"integer","description":"1-8, default 5."}
        }}
        """;
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override string ProgressLabel(JsonElement args) => "מחפש בכל השיחות";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var s = WorkSnapshot.Load(DateTime.Now);
        var query = Str(args, "query");
        var name = Str(args, "name");
        var card = s.FindClient(Str(args, "phone")) ?? s.FindClient(name);
        var phone = card?.Phone ?? Str(args, "phone");
        var all = NoteSearch.LoadAll(s.Notes, s.Now);
        var limit = Math.Clamp(Int(args, "limit") ?? 5, 1, 8);
        var hits = NoteSearch.Find(all, query, phone, s.Now, limit, Int(args, "since_days"));
        if (hits.Count == 0 && phone is null && !string.IsNullOrWhiteSpace(name))
            hits = NoteSearch.Find(all, $"{name} {query}".Trim(), null, s.Now, limit, Int(args, "since_days"));
        var sb = new StringBuilder();
        if (card is not null && !string.IsNullOrWhiteSpace(query))
        {
            var facts = Palon.Memory.FactBook.Search(s.FactsFor(card), query).Take(4).ToList();
            if (facts.Count > 0) sb.Append("Remembered facts:\n").Append(string.Join("\n", facts.Select(f => "- " + f.Text))).Append("\n\n");
        }
        if (hits.Count == 0) sb.Append("No matching call notes.");
        foreach (var h in hits)
            sb.Append($"[{h.Note.StartedLocal:ddd d MMM HH:mm}] {s.Who(h.Note.Number)}{(h.Note.Archived ? " (archive)" : "")}: {h.Snippet}\n");
        return Task.FromResult(new ToolOutcome(sb.ToString().TrimEnd()));
    }
}
