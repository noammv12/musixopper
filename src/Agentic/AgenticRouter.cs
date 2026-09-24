using Palon.Memory;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;
using Palon.Vision;

namespace Palon.Agentic;

/// <summary>
/// Turns a parsed command into a <see cref="CommandPreview"/> whose Execute
/// uses the existing stores and tools (with undo where the store allows it).
/// <see cref="Install"/> plugs this into <see cref="CommandRouter"/>.
/// </summary>
static class AgenticRouter
{
    static readonly object CacheGate = new();
    static WorkSnapshot? _cached;
    static DateTime _cachedAt;
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(45);
    static bool _installed;

    /// <summary>Wires CommandRouter to the parser/executor. Idempotent.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        CommandRouter.PreviewAsync = PreviewAsync;
        CommandRouter.Suggest = Suggest;
        void Invalidate() { lock (CacheGate) _cached = null; }
        CallbackStore.Changed += Invalidate;
        NotesStore.Changed += Invalidate;
        SalesStore.Changed += Invalidate;
        MemoryStore.Changed += Invalidate;
        TemplatesStore.Changed += Invalidate;
    }

    /// <summary>A recent snapshot (cached ~45 s, dropped when a store changes).</summary>
    public static WorkSnapshot Snapshot()
    {
        lock (CacheGate)
        {
            if (_cached is not null && DateTime.Now - _cachedAt < CacheFor) return _cached;
        }
        var fresh = WorkSnapshot.Load(DateTime.Now);
        lock (CacheGate)
        {
            _cached = fresh;
            _cachedAt = DateTime.Now;
        }
        return fresh;
    }

    public static ParseContext ContextFor(WorkSnapshot s)
    {
        IReadOnlyList<(string, string)> commands;
        try { commands = CommandStore.Load().Select(c => (c.Id, c.Label)).ToList(); }
        catch { commands = Array.Empty<(string, string)>(); }
        return new ParseContext(s.Now, s.NamedClients.Select(c => c.Name).Distinct().ToList(), commands);
    }

    public static IReadOnlyList<string> Suggest(string typed)
    {
        try
        {
            var s = Snapshot();
            var silent = Leads.Silent(s).Select(l => l.Client.Key).ToHashSet();
            var clients = s.NamedClients.Select(c => new SuggestClient(
                c.Name,
                c.LastLocal,
                c.Callbacks.Any(cb => cb.IsActive && cb.DueAtUtc.ToLocalTime().Date <= s.Now.Date),
                silent.Contains(c.Key))).ToList();
            return CommandSuggest.Rank(typed, new SuggestContext(DateTime.Now, clients, s.Templates.Select(t => t.Title).ToList(), Focus.IsOn(DateTime.UtcNow)));
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: suggest failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static async Task<CommandPreview?> PreviewAsync(string text, CancellationToken ct)
    {
        try
        {
            var s = await Task.Run(Snapshot, ct);
            // The snapshot may be up to ~45 s old; times are always resolved against the real clock.
            var parsed = CommandParser.Parse(text, ContextFor(s) with { Now = DateTime.Now });
            return parsed is null ? null : Build(parsed, s, text);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: preview failed: {ex.Message}");
            return null; // the agent loop gets a chance instead
        }
    }

    /// <summary>The preview for a parsed command; null when the command can't be carried out here.</summary>
    internal static CommandPreview? Build(ParsedCommand p, WorkSnapshot s, string raw)
    {
        var now = DateTime.Now;
        switch (p.Kind)
        {
            case CommandKind.Callback: return Callback(p, s, now);
            case CommandKind.Deal: return Deal(p, s, now);
            case CommandKind.Template: return Template(p, s);
            case CommandKind.Remember: return Remember(p);
            case CommandKind.ClientNote: return ClientNote(p, s);
            case CommandKind.Open: return Open(p, s);
            case CommandKind.ReadScreen: return Screen(p);
            case CommandKind.Salesforce: return Salesforce(p, s, now);
            case CommandKind.DraftFollowUp: return Draft(p, s, now);
            case CommandKind.Search: return Search(p, s);
            case CommandKind.SummarizeClient: return Summarize(p, s);
            case CommandKind.NextSteps:
            {
                var plan = NextSteps.Plan(s);
                var body = plan.Count == 0 ? "אין כרגע משהו פתוח — אין חזרות להיום ואין לידים חמים שמחכים." : string.Join("\n", plan.Select(i => "• " + i.ToLine(now)));
                return Show("plan", "מה עכשיו", body, "התוכנית להיום");
            }
            case CommandKind.Brief:
            {
                var brief = Rituals.Brief(s, Settings.RepName, NudgeRules.TipOfTheDay(now));
                return Show("brief", "בריף", brief.ToText(now), "בריף הבוקר");
            }
            case CommandKind.Recap:
                return Show("recap", "סיכום יום", Rituals.Recap(s).ToText(now), "סיכום היום");
            case CommandKind.Focus: return FocusPreview(p, now);
            default: return null;
        }
    }

    // ---- per intent ------------------------------------------------------------------

    static CommandPreview Callback(ParsedCommand p, WorkSnapshot s, DateTime now)
    {
        var card = p.Name is null ? null : s.FindClient(p.Name);
        var name = card?.Name ?? p.Name;
        var phone = card?.Phone;
        var when = p.WhenLocal!.Value;
        var rules = card is not null ? s.RulesFor(card) : Palon.Memory.TimeRules.For(s.TimeRules, name, phone);
        string? moved = null;
        if (Palon.Memory.TimeRules.Check(rules, when) is { } broken)
        {
            moved = $"הזזתי ל-{He.When(broken.Suggested, now)} בגלל הכלל שלך: {broken.Rule.Describe()}";
            when = broken.Suggested;
        }
        var note = (p.Text ?? "").Trim();
        var detail = new List<string>();
        if (note.Length > 0) detail.Add(note);
        else if (card is not null && s.LatestNote(card) is { } last) detail.Add($"שיחה אחרונה {He.Ago(last.StartedUtc.ToLocalTime(), now)}: {CallSummary.Line(last)}");
        if (!p.TimeExplicit) detail.Add("לא נאמרה שעה — קבעתי 10:00.");
        if (moved is not null) detail.Add(moved);
        if (card?.NextCallback is { } existing) detail.Add($"שים לב: כבר יש חזרה ל{card.Name} ב-{He.When(existing.DueAtUtc.ToLocalTime(), now)}.");
        var title = name is null ? $"תזכורת · {He.When(when, now)}" : $"חזרה ל{name} · {He.When(when, now)}";
        var finalWhen = when;
        return new CommandPreview("callback", title, string.Join("\n", detail), "קבע חזרה", ct =>
        {
            var url = Phones.WaMeUrl(phone) ?? "";
            var cb = CallbackStore.Add(note.Length > 0 ? note : name ?? "חזרה", finalWhen.ToUniversalTime(), name, phone, url, CallbackSource.Manual);
            if (cb is null) return Task.FromResult<string?>("לא הצלחתי לשמור את החזרה.");
            CommandRouter.OfferUndo($"חזרה ל{name ?? "תזכורת"} נקבעה", () =>
            {
                CallbackStore.Remove(cb.Id);
                return Task.FromResult("החזרה בוטלה");
            });
            return Task.FromResult<string?>($"נקבע: {title}.");
        });
    }

    static CommandPreview Deal(ParsedCommand p, WorkSnapshot s, DateTime now)
    {
        var card = s.FindClient(p.Name);
        var name = card?.Name ?? p.Name!;
        var previous = card?.Deals.OrderByDescending(d => d.Date).FirstOrDefault();
        var date = p.Date ?? now.Date;
        var deal = new Deal
        {
            ClientName = name,
            Date = date,
            Amount = p.Amount!.Value,
            Region = p.Region ?? previous?.Region ?? DealRegion.Pro,
            Source = p.Source ?? previous?.Source ?? DealSource.Affiliate,
            Affiliate = p.Affiliate ?? previous?.Affiliate,
            CreatedFrom = DealOrigin.Manual,
        };
        var bonus = s.Rules.FtdBonus(deal);
        var book = date.Year == now.Year && date.Month == now.Month ? s.Month : SalesStore.Get(date.Year, date.Month);
        var countAfter = (book?.Deals.Count ?? 0) + 1;
        var target = book?.Target;
        var detail = new List<string>
        {
            $"{SalesLabels.Region(deal.Region)} · {SalesLabels.Source(deal.Source)}{(p.Source is null ? " (ברירת מחדל)" : "")} · {deal.TierLabel}" +
            (deal.Affiliate is { Length: > 0 } a ? $" · {a}" : "") + $" · {He.DayMonth(date)}",
            bonus > 0 ? $"בונוס FTD: ₪{He.N(bonus)}" : "בלי בונוס FTD לפי הכללים",
            target is int t && t > 0 ? $"תהיה ההפקדה ה-{countAfter} מתוך {t}." : $"תהיה ההפקדה ה-{countAfter} החודש.",
        };
        if (book?.Deals.Any(d => HebrewText.SameName(d.ClientName, name) && d.Date.Date == date && d.Amount == deal.Amount) == true)
            detail.Add("שים לב: כבר רשומה הפקדה כזו היום.");
        return new CommandPreview("deal", $"הפקדה · {name} · ${He.N(deal.Amount)}", string.Join("\n", detail), "רשום הפקדה", ct =>
        {
            var month = SalesStore.Open(date.Year, date.Month);
            month.Deals.Add(deal);
            if (!SalesStore.SaveMonth(month)) return Task.FromResult<string?>("לא הצלחתי לשמור — קובץ המכירות לא קריא.");
            CommandRouter.OfferUndo($"הפקדה של {name} נרשמה", () =>
            {
                var m = SalesStore.Open(date.Year, date.Month);
                m.Deals.RemoveAll(d => d.Id == deal.Id);
                SalesStore.SaveMonth(m);
                return Task.FromResult("ההפקדה הוסרה");
            });
            var count = month.Deals.Count;
            var line = month.Target is int tt && tt > 0 ? $"{count}/{tt} החודש" : $"{count} החודש";
            return Task.FromResult<string?>($"נרשם. {line}{(bonus > 0 ? $" · +₪{He.N(bonus)}" : "")}.");
        });
    }

    /// <summary>The template that best matches a query ("הפקדה", "פתיחה ישראל", "questionnaire").</summary>
    internal static MessageTemplate? MatchTemplate(IReadOnlyList<MessageTemplate> templates, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var aliases = new (string Word, string IdPart)[]
        {
            ("הפקדה", "deposit"), ("הפקדות", "deposit"), ("העברה", "deposit"), ("פתיחה", "open"), ("פתיחת", "open"),
            ("שאלון", "questionnaire"), ("ישראל", "israel"), ("פרו", "pro"),
        };
        MessageTemplate? best = null;
        var bestScore = 0.0;
        foreach (var t in templates)
        {
            var hay = $"{t.Title} {t.Tag} {t.Region} {t.Id}";
            double score = HebrewText.Score(q, hay) * 2;
            if (HebrewText.Normalize(t.Title) == HebrewText.Normalize(q)) score += 5;
            foreach (var (word, idPart) in aliases)
                if (HebrewText.Tokens(q).Contains(word) && t.Id.Contains(idPart, StringComparison.OrdinalIgnoreCase)) score += 1.5;
            if (score > bestScore) { best = t; bestScore = score; }
        }
        return bestScore > 0 ? best : null;
    }

    static CommandPreview? Template(ParsedCommand p, WorkSnapshot s)
    {
        var template = MatchTemplate(s.Templates, p.TemplateQuery ?? "");
        if (template is null) return null;
        var card = p.Name is null ? null : s.FindClient(p.Name);
        var name = card?.Name ?? p.Name;
        var filled = TemplateFill.Fill(template, TemplateFill.FirstName(name));
        var detail = filled.Length > 400 ? filled[..400] + "…" : filled;
        return new CommandPreview("template", name is null ? $"תבנית \"{template.Title}\"" : $"תבנית \"{template.Title}\" ל{name}", detail, "העתק", ct =>
        {
            if (!AgenticHost.CopyText(filled)) return Task.FromResult<string?>("ההעתקה נכשלה.");
            Terminal.TemplateLearningStore.LogCopy(template, name, card?.Phone, filled, null, "command");
            return Task.FromResult<string?>("הועתק — הדבק בוואטסאפ.");
        });
    }

    static CommandPreview Remember(ParsedCommand p)
    {
        var text = p.Text!;
        var rule = Palon.Memory.TimeRules.TryParse(text);
        var detail = rule is null ? "יישמר ב\"עליך\" — אני רואה את זה בכל שיחה." : $"כלל שייאכף כשקובעים חזרות: {rule.Describe()}";
        return new CommandPreview("remember", $"לזכור: {text}", detail, "זכור", ct =>
        {
            var (item, added) = MemoryStore.RememberFromChat(text, rule is null ? ProfileSection.Preferences : ProfileSection.Rules, "explicit", "command bar", rule);
            if (!added) return Task.FromResult<string?>("כבר זוכר את זה.");
            CommandRouter.OfferUndo("נשמר בזיכרון", () =>
            {
                MemoryStore.ForgetFromChat(item.Text, "undo from the command bar");
                return Task.FromResult("נמחק מהזיכרון");
            });
            return Task.FromResult<string?>("זוכר.");
        });
    }

    static CommandPreview ClientNote(ParsedCommand p, WorkSnapshot s)
    {
        var card = s.FindClient(p.Name);
        var name = card?.Name ?? p.Name!;
        var phone = card?.Phone;
        var text = p.Text!;
        return new CommandPreview("note", $"הערה על {name}", text, "שמור", ct =>
        {
            var key = FactBook.KeyFor(phone, name);
            if (key is null) return Task.FromResult<string?>("לא הצלחתי לזהות את הלקוח.");
            var fact = new ClientFact
            {
                ClientKey = key,
                ClientName = name,
                Phone = phone,
                Type = "other",
                Text = text,
                Hash = HebrewText.Hash(text),
                ValidAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                Source = "manual",
            };
            MemoryStore.MutateFacts(all => { all.Add(fact); return true; });
            CommandRouter.OfferUndo($"הערה על {name} נשמרה", () =>
            {
                MemoryStore.MutateFacts(all => all.RemoveAll(f => f.Id == fact.Id));
                return Task.FromResult("ההערה נמחקה");
            });
            return Task.FromResult<string?>($"שמרתי על {name}.");
        });
    }

    static readonly Dictionary<string, string> PageTitles = new()
    {
        ["today"] = "היום", ["callbacks"] = "חזרות", ["month"] = "החודש", ["clients"] = "לקוחות",
        ["templates"] = "תבניות", ["coaching"] = "אימון", ["memory"] = "זיכרון", ["settings"] = "הגדרות",
    };

    static CommandPreview? Open(ParsedCommand p, WorkSnapshot s)
    {
        var target = p.Target!;
        if (target.StartsWith("cmd:", StringComparison.Ordinal))
        {
            var id = target[4..];
            return new CommandPreview("open", $"פתח {p.Text}", "פקודה שמורה", "פתח", ct =>
            {
                var cmd = CommandStore.Load().FirstOrDefault(c => c.Id == id);
                return Task.FromResult<string?>(cmd is not null && CommandStore.Execute(cmd) ? null : "הפתיחה נכשלה.");
            });
        }
        if (target == "client")
        {
            var card = s.FindClient(p.Name);
            var name = card?.Name ?? p.Name!;
            return new CommandPreview("open", $"פתח את {name}", card is null ? "" : ClientSummaries.Build(s, card).ToText(), "פתח", ct =>
            {
                AgenticHost.OpenPage?.Invoke("clients", name);
                return Task.FromResult<string?>(null);
            });
        }
        var title = PageTitles.TryGetValue(target, out var t) ? t : target;
        return new CommandPreview("open", $"פתח {title}", "", "פתח", ct =>
        {
            if (AgenticHost.OpenPage is null) return Task.FromResult<string?>("החלון הראשי לא זמין.");
            AgenticHost.OpenPage(target, null);
            return Task.FromResult<string?>(null);
        });
    }

    static CommandPreview Screen(ParsedCommand p)
    {
        if (p.Receipt)
            return new CommandPreview("screen", "קריאת קבלה", "סמן את הקבלה במסך ואשר. אמלא טופס הפקדה לבדיקה — שום דבר לא נשמר לבד.", "סמן", async ct =>
            {
                var (receipt, error) = await ScreenReader.ReceiptAsync(false, ct);
                if (receipt is null) return error ?? "בוטל — לא נשלח כלום.";
                ScreenReader.OpenDealSheet(receipt);
                return ScreenReader.ReceiptSummary(receipt);
            });
        return new CommandPreview("screen", "קריאת מסך", "סמן אזור במסך ואשר — שום דבר לא נשלח בלי אישור.", "סמן", async ct =>
        {
            var (read, error) = await ScreenReader.ReadAsync(p.Text, false, ct);
            return read?.Answer ?? error ?? "בוטל — לא נשלח כלום.";
        });
    }

    static CommandPreview? Salesforce(ParsedCommand p, WorkSnapshot s, DateTime now)
    {
        CallNote? note;
        string who;
        if (p.Name is not null)
        {
            var card = s.FindClient(p.Name);
            note = s.LatestNote(card);
            who = card?.Name ?? p.Name;
            if (note is null)
                return new CommandPreview("salesforce", $"Salesforce · {who}", $"אין לי שיחה מתועדת עם {who} לתעד.", "סגור", _ => Task.FromResult<string?>(null));
        }
        else
        {
            note = s.Notes.OrderByDescending(n => n.StartedUtc).FirstOrDefault();
            if (note is null) return null;
            who = s.Who(note.Number);
        }
        var callback = s.Callbacks.Where(c => c.IsActive && c.CallNoteId == note.Id).Select(c => (DateTime?)c.DueAtUtc.ToLocalTime()).FirstOrDefault()
                       ?? (note.ProposedCallback is { State: "accepted" } pc ? pc.WhenUtc.ToLocalTime() : null);
        var detail = $"שיחה {He.Ago(note.StartedUtc.ToLocalTime(), now)} ({He.Duration(note.DurationSec)}): {CallSummary.Line(note)}\n" +
                     "אראה לך כל שינוי לפני שנכתב משהו.";
        var n = note;
        return new CommandPreview("salesforce", $"תיעוד ב-Salesforce · {who}", detail, "הכן תיעוד", ct =>
        {
            if (AgenticHost.LogToSalesforce is null) return Task.FromResult<string?>("חיבור Salesforce לא זמין כאן.");
            AgenticHost.LogToSalesforce(n, callback);
            return Task.FromResult<string?>(null);
        });
    }

    static CommandPreview? Draft(ParsedCommand p, WorkSnapshot s, DateTime now)
    {
        if (p.Name is null) return null;
        var card = s.FindClient(p.Name);
        if (card is null)
            return new CommandPreview("draft", $"הודעה ל{p.Name}", $"לא מצאתי את {p.Name} בשיחות שלך. אפשר לבקש מ-Palon לנסח בלי הקשר.", "סגור", _ => Task.FromResult<string?>(null));
        var note = s.LatestNote(card);
        if (note is null)
            return new CommandPreview("draft", $"הודעה ל{card.Name}", $"אין לי שיחה מתועדת עם {card.Name} — אין ממה לנסח.", "סגור", _ => Task.FromResult<string?>(null));
        var cached = p.Text is null ? FollowUpDrafts.Cached(card.Key, note.Id) : null;
        var detail = cached?.Text ?? $"מתוך השיחה {He.Ago(note.StartedUtc.ToLocalTime(), now)}: {CallSummary.Line(note)}" + (p.Text is { } ins ? $"\nבקשה: {ins}" : "");
        return new CommandPreview("draft", $"הודעת המשך ל{card.Name}", detail, cached is null ? "נסח" : "העתק", async ct =>
        {
            var (text, error) = await FollowUpDrafts.GetOrCreateAsync(s, card, p.Text, ct);
            if (text is null) return error;
            AgenticHost.CopyText(text);
            AgenticHost.ShowText?.Invoke($"הודעה ל{card.Name}", text);
            return text;
        });
    }

    static CommandPreview Search(ParsedCommand p, WorkSnapshot s)
    {
        var card = p.Name is null ? null : s.FindClient(p.Name);
        var all = NoteSearch.LoadAll(s.Notes, s.Now);
        var hits = NoteSearch.Find(all, p.Text, card?.Phone, s.Now, limit: 5);
        // A client with no phone on file: fall back to his name inside the text.
        if (p.Name is not null && card?.Phone is null)
            hits = NoteSearch.Find(all, $"{p.Name} {p.Text}".Trim(), null, s.Now, limit: 5);
        var who = card?.Name ?? p.Name;
        var title = who is null ? $"חיפוש: {p.Text}" : $"מה {who} אמר{(string.IsNullOrEmpty(p.Text) ? "" : " על " + p.Text)}";
        var body = NoteSearch.Format(hits, s);
        // Remembered facts often answer "what did X say about Y" better than a transcript.
        if (card is not null && p.Text is { Length: > 0 } q)
        {
            var facts = FactBook.Search(s.FactsFor(card), q).Take(3).Select(f => "• " + f.Text).ToList();
            if (facts.Count > 0) body = "מהזיכרון:\n" + string.Join("\n", facts) + "\n\nמהשיחות:\n" + body;
        }
        return Show("search", title, body, title);
    }

    static CommandPreview? Summarize(ParsedCommand p, WorkSnapshot s)
    {
        var card = s.FindClient(p.Name);
        if (card is null) return null;
        var summary = ClientSummaries.Build(s, card);
        return new CommandPreview("client", card.Name, summary.ToText(), "פתח כרטיס", ct =>
        {
            AgenticHost.OpenPage?.Invoke("clients", card.Name);
            return Task.FromResult<string?>(null);
        });
    }

    static CommandPreview FocusPreview(ParsedCommand p, DateTime now)
    {
        if (p.Off)
            return new CommandPreview("focus", "סיום פוקוס", "אחזור להציע דברים כשיש משהו ששווה.", "סיים", ct =>
            {
                Focus.Clear();
                return Task.FromResult<string?>("חזרתי.");
            });
        var length = p.Duration ?? TimeSpan.FromHours(1);
        var until = now + length;
        return new CommandPreview("focus", $"פוקוס עד {He.Clock(until)}", "אשתוק. רק חזרות שקבעת יגיעו אליך.", "התחל", ct =>
        {
            var before = AgenticState.Load().FocusUntilUtc;
            Focus.Set(length);
            CommandRouter.OfferUndo("פוקוס הופעל", () =>
            {
                AgenticState.Mutate(st => st.FocusUntilUtc = before);
                return Task.FromResult("הפוקוס בוטל");
            });
            return Task.FromResult<string?>($"בפוקוס עד {He.Clock(until)}.");
        });
    }

    /// <summary>A read-only preview: the answer is already in Detail; confirming shows it in full.</summary>
    static CommandPreview Show(string kind, string title, string body, string showTitle) =>
        new(kind, title, body, "הצג", ct =>
        {
            AgenticHost.ShowText?.Invoke(showTitle, body);
            return Task.FromResult<string?>(AgenticHost.ShowText is null ? body : null);
        });
}
