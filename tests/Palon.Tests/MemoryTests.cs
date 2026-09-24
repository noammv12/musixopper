using Palon;
using Palon.Memory;
using Xunit;

namespace Palon.Tests;

public class HebrewTextTests
{
    [Fact]
    public void Normalize_strips_niqqud_folds_finals_and_punctuation()
    {
        Assert.Equal("שלומ עולמ", HebrewText.Normalize("שָׁלוֹם, עוֹלָם!"));
        Assert.Equal(HebrewText.Hash("תקציב 2,000$"), HebrewText.Hash("  תקציב 2000 "));
    }

    [Fact]
    public void Tokens_strip_hebrew_prefixes_for_recall()
    {
        Assert.True(HebrewText.SameName("דני", "ולדני"));
        Assert.True(HebrewText.Score("גירושין", "באמצע תהליך גירושים") > 0 || HebrewText.Score("גירוש", "באמצע תהליך גירושים") > 0);
        Assert.Equal(1, HebrewText.Score("בנק", "רוצה לסחור מהבנק"));
        Assert.False(HebrewText.SameName("דני", "משה"));
    }
}

public class TimeRuleTests
{
    [Theory]
    [InlineData("אל תתקשר לדני לפני 12", "דני", 12, 0, true)]
    [InlineData("לא להתקשר לדני כהן אחרי 18:30", "דני כהן", 18, 30, false)]
    [InlineData("Don't call Dani before 11:30am", "Dani", 11, 30, true)]
    [InlineData("never call Dani after 6", "Dani", 18, 0, false)]
    public void Parses_time_window_rules(string text, string who, int h, int m, bool notBefore)
    {
        var rule = TimeRules.TryParse(text);
        Assert.NotNull(rule);
        Assert.Equal(who, rule!.Who);
        var at = new TimeSpan(h, m, 0);
        if (notBefore) Assert.Equal(at, rule.NotBefore);
        else Assert.Equal(at, rule.NotAfter);
    }

    [Fact]
    public void Non_rules_parse_to_null()
    {
        Assert.Null(TimeRules.TryParse("המנהל שלי הוא יוסי"));
        Assert.Null(TimeRules.TryParse("I like short answers"));
    }

    [Fact]
    public void Check_moves_a_violating_time_to_the_first_allowed_slot()
    {
        var rules = TimeRules.For(new[] { new TimeRule("דני", NotBefore: TimeSpan.FromHours(12)) }, "דני לוי", null);
        Assert.Single(rules);
        var morning = new DateTime(2026, 9, 24, 10, 0, 0);
        var broken = TimeRules.Check(rules, morning);
        Assert.NotNull(broken);
        Assert.Equal(new DateTime(2026, 9, 24, 12, 0, 0), broken!.Value.Suggested);
        Assert.Null(TimeRules.Check(rules, new DateTime(2026, 9, 24, 13, 0, 0)));
    }

    [Fact]
    public void Not_after_rolls_to_next_day()
    {
        var rules = new[] { new TimeRule("x", NotAfter: TimeSpan.FromHours(18)) };
        Assert.Equal(new DateTime(2026, 9, 25, 10, 0, 0), TimeRules.NextAllowed(rules, new DateTime(2026, 9, 24, 19, 0, 0)));
    }

    [Fact]
    public void Rules_match_by_phone_too_and_skip_other_clients()
    {
        var all = new[] { new TimeRule("", "050-123-4567", NotBefore: TimeSpan.FromHours(12)), new TimeRule("משה", NotBefore: TimeSpan.FromHours(9)) };
        Assert.Single(TimeRules.For(all, null, "+972501234567"));
        Assert.Empty(TimeRules.For(all, "דני", "0529999999"));
    }

    [Fact]
    public void Quick_picks_obey_rules()
    {
        var now = new DateTime(2026, 9, 24, 8, 0, 0); // Thursday
        var picks = CallbackPlanner.QuickPicks(now);
        var rules = new[] { new TimeRule("דני", NotBefore: TimeSpan.FromHours(12)) };
        var ruled = TimeRules.Apply(picks, rules);
        Assert.All(ruled.Where(p => p.Enabled), p => Assert.True(TimeRules.Allowed(rules[0], p.DueLocal)));
        Assert.Contains(ruled, p => p.Key == "rule");
        Assert.Equal(picks, TimeRules.Apply(picks, Array.Empty<TimeRule>()));
    }
}

public class ProfileBookTests
{
    static readonly DateTime Now = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Remember_dedups_attaches_rules_and_logs_reasons()
    {
        var d = new ProfileData();
        var (item, added) = ProfileBook.Remember(d, "אל תתקשר לדני לפני 12", ProfileSection.Preferences, "explicit", "user asked", Now);
        Assert.True(added);
        Assert.Equal(ProfileSection.Rules, item.Section);
        Assert.NotNull(item.Rule);
        var (_, again) = ProfileBook.Remember(d, "אל תתקשר לדני, לפני 12!", ProfileSection.Rules, "explicit", "dup", Now);
        Assert.False(again);
        Assert.Single(d.Items);
        Assert.Equal("user asked", d.History.Single().Reason);
    }

    [Fact]
    public void Undo_reverts_add_edit_and_delete()
    {
        var d = new ProfileData();
        var (item, _) = ProfileBook.Remember(d, "המנהל שלי הוא יוסי", ProfileSection.People, "explicit", "r", Now);
        ProfileBook.Edit(d, item.Id, "המנהלת שלי היא רותי", "manager changed", Now);
        Assert.Equal("המנהלת שלי היא רותי", d.Items.Single().Text);
        ProfileBook.Undo(d, d.History.Last().Id, Now);
        Assert.Equal("המנהל שלי הוא יוסי", d.Items.Single().Text);

        ProfileBook.Delete(d, item.Id, "gone", Now);
        Assert.Empty(d.Items);
        ProfileBook.Undo(d, d.History.Last(c => c.Op == "delete").Id, Now);
        Assert.Single(d.Items);

        var addId = d.History.First(c => c.Op == "add").Id;
        ProfileBook.Undo(d, addId, Now);
        Assert.Empty(d.Items);
    }

    [Fact]
    public void Suggestions_need_quote_confidence_and_are_never_resuggested_after_reject()
    {
        var reply = "```json\n{\"suggestions\":[{\"section\":\"style\",\"text\":\"מעדיף תשובות קצרות\",\"quote\":\"תענה קצר\",\"confidence\":0.9}," +
                    "{\"section\":\"rules\",\"text\":\"no quote\",\"confidence\":0.9}," +
                    "{\"section\":\"people\",\"text\":\"unsure thing\",\"quote\":\"x\",\"confidence\":0.3}]}\n```";
        var parsed = ProfileBook.ParseSuggestions(reply, Now);
        Assert.Equal(2, parsed.Count);
        var d = new ProfileData();
        Assert.Equal(1, ProfileBook.AddSuggestions(d, parsed));
        Assert.Empty(d.Items); // suggestions never write the profile by themselves
        ProfileBook.Reject(d, d.Suggestions.Single().Id);
        Assert.Equal(0, ProfileBook.AddSuggestions(d, ProfileBook.ParseSuggestions(reply, Now)));
        Assert.Empty(ProfileBook.ParseSuggestions("not json", Now));
    }

    [Fact]
    public void Approve_moves_a_suggestion_into_the_profile()
    {
        var d = new ProfileData();
        ProfileBook.AddSuggestions(d, new[] { new ProfileSuggestion("s1", ProfileSection.Style, "מעדיף וואטסאפ על פני מייל", "תשלח בוואטסאפ", 0.8, Now) });
        var item = ProfileBook.Approve(d, "s1", Now);
        Assert.NotNull(item);
        Assert.Equal("suggestion", item!.Source);
        Assert.Empty(d.Suggestions);
    }

    [Fact]
    public void Prompt_is_capped_and_pinned_first()
    {
        var d = new ProfileData();
        for (var i = 0; i < 60; i++) ProfileBook.Remember(d, $"העדפה מספר {i} עם עוד קצת טקסט כדי למלא", ProfileSection.Preferences, "manual", "r", Now.AddMinutes(i));
        var pinned = d.Items[0];
        ProfileBook.SetPinned(d, pinned.Id, true, Now);
        var prompt = ProfileBook.RenderForPrompt(d, 800);
        Assert.True(prompt.Length <= 900);
        Assert.Contains("more lines not shown", prompt);
        Assert.Contains(pinned.Text, prompt);
    }
}

public class ClientFactTests
{
    static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Extract_strips_facts_line_and_keeps_callback_line()
    {
        var reply = "אין ניסיון - רוצה לפתוח ב2,000$\n" +
                    "CALLBACK: {\"when_iso\":\"2026-09-02T11:00\",\"phrase\":\"מחר ב-11\"}\n" +
                    "FACTS: {\"client\":\"דני\",\"facts\":[{\"type\":\"budget\",\"text\":\"תקציב 2,000$\",\"quote\":\"2,000 דולר\",\"valid_at\":null,\"replaces\":[0]},{\"type\":\"weird\",\"text\":\"גרוש טרי\"}]}";
        var (rest, facts) = FactBook.Extract(reply);
        Assert.DoesNotContain("FACTS", rest);
        Assert.Contains("CALLBACK:", rest);
        Assert.NotNull(facts);
        Assert.Equal("דני", facts!.ClientName);
        Assert.Equal(2, facts.Facts.Count);
        Assert.Equal("other", facts.Facts[1].Type);
        Assert.Equal(new[] { 0 }, facts.Facts[0].Replaces);
    }

    [Fact]
    public void Malformed_or_absent_facts_line_never_loses_the_note()
    {
        Assert.Null(FactBook.Extract("note only").Facts);
        var (rest, facts) = FactBook.Extract("note\nFACTS: {broken");
        Assert.Equal("note", rest);
        Assert.Null(facts);
    }

    static FactsPayload Payload(params ParsedFact[] f) => new("דני", f);

    [Fact]
    public void Hash_dedup_against_store_and_within_batch()
    {
        var all = new List<ClientFact>();
        var key = FactBook.KeyFor("0501234567", "דני")!;
        var p = Payload(new ParsedFact("budget", "תקציב 2,000$", null, null, Array.Empty<int>()),
                        new ParsedFact("budget", "תקציב 2000", null, null, Array.Empty<int>()));
        var first = FactBook.Apply(all, key, "דני", "0501234567", p, Array.Empty<string>(), "call:a", T0, T0);
        Assert.Single(first.Added);
        Assert.Equal(1, first.Duplicates);
        var second = FactBook.Apply(all, key, "דני", "0501234567", Payload(new ParsedFact("budget", "תקציב: 2,000 $", null, null, Array.Empty<int>())), Array.Empty<string>(), "call:b", T0.AddDays(1), T0.AddDays(1));
        Assert.Empty(second.Added);
        Assert.Single(all);
    }

    [Fact]
    public void Replaced_fact_is_invalidated_not_deleted_via_integer_id_map()
    {
        var all = new List<ClientFact>();
        var key = FactBook.KeyFor("0501234567", null)!;
        FactBook.Apply(all, key, "דני", "0501234567", Payload(new ParsedFact("budget", "תקציב 2,000$", null, null, Array.Empty<int>())), Array.Empty<string>(), "call:a", T0, T0);
        var (block, map) = FactBook.KnownFactsBlock(all);
        Assert.Contains("[0]", block);
        Assert.DoesNotContain(all[0].Id, block); // GUIDs never reach the model

        var later = T0.AddDays(7);
        var applied = FactBook.Apply(all, key, "דני", "0501234567", Payload(new ParsedFact("budget", "תקציב 5,000$", null, null, new[] { 0, 9 })), map, "call:b", later, later);
        Assert.Single(applied.Invalidated);
        Assert.Equal(2, all.Count);
        var old = all.Single(f => f.Text.Contains("2,000"));
        Assert.Equal(later, old.InvalidAt);
        Assert.Equal(later, old.ExpiredAt);
        Assert.Single(all, f => f.IsActive);
        Assert.Contains("5,000", FactBook.RenderClientBlock("דני", all));
        Assert.DoesNotContain("2,000", FactBook.RenderClientBlock("דני", all));
    }

    [Fact]
    public void Late_stale_fact_is_expired_immediately()
    {
        var all = new List<ClientFact>();
        var key = "p:501234567";
        FactBook.Apply(all, key, null, null, Payload(new ParsedFact("status", "פתח חשבון", null, T0.AddDays(10), Array.Empty<int>())), Array.Empty<string>(), "call:a", T0.AddDays(10), T0.AddDays(10));
        var map = FactBook.KnownFactsBlock(all).IdMap;
        FactBook.Apply(all, key, null, null, Payload(new ParsedFact("status", "עוד לא פתח חשבון", null, T0, new[] { 0 })), map, "call:b", T0, T0.AddDays(11));
        Assert.Single(all, f => f.IsActive);
        Assert.Equal("פתח חשבון", all.Single(f => f.IsActive).Text);
    }

    [Fact]
    public void Correct_and_invalidate_keep_history()
    {
        var all = new List<ClientFact>();
        FactBook.Apply(all, "n:דני", "דני", null, Payload(new ParsedFact("family", "נשוי", null, null, Array.Empty<int>())), Array.Empty<string>(), "call:a", T0, T0);
        var fixedFact = FactBook.Correct(all, all[0].Id, "גרוש", T0.AddDays(1));
        Assert.NotNull(fixedFact);
        Assert.Equal(2, all.Count);
        Assert.True(FactBook.Invalidate(all, fixedFact!.Id, T0.AddDays(2)));
        Assert.Empty(all.Where(f => f.IsActive));
        Assert.Equal(2, FactBook.Search(all, null, includeHistory: true).Count);
    }

    [Fact]
    public void Search_finds_by_hebrew_prefix_and_client_lookup_by_phone_or_name()
    {
        var all = new List<ClientFact>();
        FactBook.Apply(all, FactBook.KeyFor("0501234567", null)!, "דני כהן", "0501234567",
            Payload(new ParsedFact("objection", "חושש מהבנק", null, null, Array.Empty<int>())), Array.Empty<string>(), "call:a", T0, T0);
        Assert.Single(FactBook.Search(all, "בנק"));
        Assert.Single(FactBook.ForClient(all, null, "+972-50-123-4567"));
        Assert.Single(FactBook.ForClient(all, "דני", null));
        Assert.Empty(FactBook.ForClient(all, "משה", "0529999999"));
    }
}

public class MemorySeparationTests
{
    [Fact]
    public void About_you_block_and_client_block_never_mix()
    {
        var profile = new ProfileData();
        ProfileBook.Remember(profile, "המנהל שלי הוא יוסי", ProfileSection.People, "explicit", "r", DateTime.UtcNow);
        var facts = new List<ClientFact>();
        FactBook.Apply(facts, "n:דני", "דני", null, new FactsPayload("דני", new[] { new ParsedFact("budget", "תקציב 3,000$", null, null, Array.Empty<int>()) }),
            Array.Empty<string>(), "call:a", DateTime.UtcNow, DateTime.UtcNow);

        var aboutYou = ProfileBook.RenderForPrompt(profile);
        var client = FactBook.RenderClientBlock("דני", facts);
        Assert.StartsWith("ABOUT THE USER", aboutYou);
        Assert.StartsWith("CLIENT MEMORY", client);
        Assert.DoesNotContain("3,000", aboutYou);
        Assert.DoesNotContain("יוסי", client);
        Assert.Contains("NOT about the user", client);
        // The reflection prompt (profile inference) refuses client facts.
        Assert.Contains("Never extract facts about clients", ProfileBook.ReflectionPrompt);
        Assert.Contains("never about the salesperson", FactBook.SummaryInstruction);
    }
}

public class SummaryTrailerOrderTests
{
    const string Cb = "CALLBACK: {\"when_iso\":\"2026-09-25T11:00\",\"phrase\":\"מחר ב-11\",\"reason\":\"לחזור\"}";
    const string Fa = "FACTS: {\"client\":null,\"facts\":[{\"type\":\"budget\",\"text\":\"2000$\",\"quote\":\"2000 דולר\",\"valid_at\":null,\"replaces\":[]}]}";
    const string Co = "COACH: {\"objections\":[],\"client_questions\":[],\"next_step\":{\"agreed\":false}}";

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    public void All_trailers_are_stripped_in_any_order(int a, int b, int c)
    {
        var t = new[] { Cb, Fa, Co };
        var reply = $"הערה קצרה על השיחה.\n{t[a]}\n{t[b]}\n{t[c]}";
        var (s1, facts) = FactBook.Extract(reply);
        var (s2, coachLine) = Palon.Coaching.CoachExtract.Split(s1);
        var (s3, proposal) = Palon.Notes.CallbackProposals.Extract(s2, new DateTime(2026, 9, 24, 15, 0, 0));
        Assert.Equal("הערה קצרה על השיחה.", s3);
        Assert.NotNull(facts);
        Assert.NotNull(coachLine);
        Assert.NotNull(proposal);
    }

    [Fact]
    public void Broken_facts_line_does_not_lose_coach_or_callback()
    {
        var reply = "note\nFACTS: {broken\n" + Co + "\n" + Cb;
        var (s1, facts) = FactBook.Extract(reply);
        var (s2, coachLine) = Palon.Coaching.CoachExtract.Split(s1);
        var (s3, proposal) = Palon.Notes.CallbackProposals.Extract(s2, new DateTime(2026, 9, 24, 15, 0, 0));
        Assert.Null(facts);
        Assert.NotNull(coachLine);
        Assert.NotNull(proposal);
        Assert.Equal("note", s3);
    }
}
