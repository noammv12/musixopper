using System.IO;
using Palon.Terminal;
using Xunit;

namespace Palon.Tests;

public class TemplateLearningTests
{
    static readonly DateTime T0 = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    static MessageTemplate Tpl(string text = "שורה א\nשורה ב\nשורה ג") =>
        new() { Id = "t1", Title = "בדיקה", Mode = TemplateMode.Prepend, Text = text };

    static TemplateSend Send(MessageTemplate t, string name, string? final, int minutes = 0, string? phone = null)
    {
        var filled = TemplateFill.Fill(t, TemplateFill.FirstName(name));
        return new TemplateSend(Guid.NewGuid().ToString("n"), T0.AddMinutes(minutes), t.Id, t.Version, name, phone, filled,
            final is null ? null : final.Replace("{n}", TemplateFill.FirstName(name)));
    }

    static TemplateSend Free(string text, string? name = null, int minutes = 0) =>
        new(Guid.NewGuid().ToString("n"), T0.AddMinutes(minutes), null, 0, name, null, text, text);

    // ---- logging -------------------------------------------------------------------

    [Fact]
    public void LogCopy_PersistsSend_AndDropsUnchangedFinalText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"palon-tl-{Guid.NewGuid():n}.json");
        TemplateLearningStore.PathOverride = path;
        try
        {
            var t = Tpl();
            t.Version = 3;
            var filled = TemplateFill.Fill(t, "דני");
            Assert.NotNull(TemplateLearningStore.LogCopy(t, "דני לוי", "0541234567", filled, filled, "dock"));
            Assert.NotNull(TemplateLearningStore.LogCopy(t, "דני", null, filled, filled + "\nתוספת", "templates-edit"));
            var sends = TemplateLearningStore.Sends();
            Assert.Equal(2, sends.Count);
            Assert.Equal("t1", sends[0].TemplateId);
            Assert.Equal(3, sends[0].Version);
            Assert.Equal("0541234567", sends[0].Phone);
            Assert.Null(sends[0].FinalText);
            Assert.False(sends[0].Edited);
            Assert.True(sends[1].Edited);

            TemplateLearningStore.Decide("k", accepted: false);
            Assert.Contains("k", TemplateLearningStore.Decided());
            TemplateLearningStore.Undecide("k");
            Assert.DoesNotContain("k", TemplateLearningStore.Decided());
        }
        finally
        {
            TemplateLearningStore.PathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Versions_BumpOnlyWhenWordingChanges()
    {
        var old = Tpl();
        var same = old.Clone();
        same.Tag = "tag only";
        Assert.Equal(1, TemplateVersions.Next(old, same, T0).Version);

        var changed = old.Clone();
        changed.Text = "חדש";
        var next = TemplateVersions.Next(old, changed, T0, "הצעה של Palon");
        Assert.Equal(2, next.Version);
        Assert.Single(next.History);
        Assert.Equal(old.Text, next.History[0].Text);
        Assert.Equal(1, next.History[0].Version);
    }

    // ---- outcomes ------------------------------------------------------------------

    [Fact]
    public void Outcome_LinksDepositWithin14Days_AndNextCallByPhone()
    {
        var t = Tpl();
        var s = Send(t, "דני לוי", null, phone: "+972-54-123-4567");
        var deals = new List<(string, DateTime)> { ("דני לוי", T0.Date.AddDays(10)) };
        var calls = new List<(DateTime, string?)> { (T0.AddDays(2), "0541234567") };
        var o = TemplateLearning.Outcome(s, deals, calls);
        Assert.True(o.Deposit);
        Assert.True(o.NextCall);

        var late = new List<(string, DateTime)> { ("דני לוי", T0.Date.AddDays(20)) };
        var other = new List<(string, DateTime)> { ("משה כהן", T0.Date.AddDays(1)) };
        var before = new List<(DateTime, string?)> { (T0.AddDays(-1), "0541234567") };
        Assert.False(TemplateLearning.Outcome(s, late, before).Deposit);
        Assert.False(TemplateLearning.Outcome(s, other, before).Deposit);
        Assert.False(TemplateLearning.Outcome(s, other, before).NextCall);
    }

    [Fact]
    public void Stats_HiddenUnder20Uses()
    {
        var t = Tpl();
        var sends = Enumerable.Range(0, 19).Select(i => Send(t, $"לקוח{i} משפחה", null, i)).ToList();
        var deals = new List<(string, DateTime)> { ("לקוח1 משפחה", T0.Date.AddDays(3)), ("לקוח2 משפחה", T0.Date.AddDays(3)) };
        var calls = new List<(DateTime, string?)>();
        Assert.Null(TemplateLearning.Stats("t1", sends, deals, calls));
        sends.Add(Send(t, "לקוח19 משפחה", null, 19));
        var st = TemplateLearning.Stats("t1", sends, deals, calls);
        Assert.NotNull(st);
        Assert.Equal(20, st!.Value.Uses);
        Assert.Equal(2, st.Value.Deposits);
        Assert.Equal(0.1, st.Value.Rate, 3);
        Assert.InRange(st.Value.WilsonLower, 0.0, 0.1);
    }

    // ---- edit recurrence ---------------------------------------------------------------

    [Fact]
    public void SameEditThreeTimes_ProposesUpdate_WithNameNeutralDiff()
    {
        var t = Tpl();
        var edited = "היי {n}!\nשורה א\nשורה ב חדשה\nשורה ג";
        var sends = new List<TemplateSend>
        {
            Send(t, "דני לוי", edited, 1),
            Send(t, "משה", edited, 2),
        };
        var decided = new HashSet<string>();
        Assert.Empty(TemplateLearning.EditProposals(new[] { t }, sends, decided));

        sends.Add(Send(t, "רינה כהן", edited, 3));
        var p = Assert.Single(TemplateLearning.EditProposals(new[] { t }, sends, decided));
        Assert.Equal(ProposalKind.Edit, p.Kind);
        Assert.Equal(3, p.Count);
        Assert.Equal("שורה א\nשורה ב חדשה\nשורה ג", p.After);

        decided.Add(p.Key);
        Assert.Empty(TemplateLearning.EditProposals(new[] { t }, sends, decided));
    }

    [Fact]
    public void DifferentEdits_DoNotCombine_AndOldVersionsIgnored()
    {
        var t = Tpl();
        var sends = new List<TemplateSend>
        {
            Send(t, "דני", "היי {n}!\nשורה א\nX\nשורה ב\nשורה ג", 1),
            Send(t, "דני", "היי {n}!\nשורה א\nY\nשורה ב\nשורה ג", 2),
            Send(t, "דני", "היי {n}!\nשורה א\nשורה ב\nשורה ג\nZ", 3),
        };
        Assert.Empty(TemplateLearning.EditProposals(new[] { t }, sends, new HashSet<string>()));

        var same = Enumerable.Range(0, 3).Select(i => Send(t, "דני", "היי {n}!\nשורה א\nשורה ג", i)).ToList();
        var v2 = t.Clone();
        v2.Version = 2;
        Assert.Empty(TemplateLearning.EditProposals(new[] { v2 }, same, new HashSet<string>()));
        Assert.Single(TemplateLearning.EditProposals(new[] { t }, same, new HashSet<string>()));
    }

    [Fact]
    public void ReplaceMode_EditAppliesToTemplateText()
    {
        var t = new MessageTemplate { Id = "r", Mode = TemplateMode.Replace, Head = "היי ", Text = "היי !\nאחת\nשתיים" };
        var sends = Enumerable.Range(0, 3).Select(i => Send(t, "דני", "היי {n}!\nאחת\nשתיים\nשלוש", i)).ToList();
        var p = Assert.Single(TemplateLearning.EditProposals(new[] { t }, sends, new HashSet<string>()));
        Assert.Equal("היי !\nאחת\nשתיים\nשלוש", p.After);
    }

    [Fact]
    public void WhitespaceOnlyEdits_AreIgnored()
    {
        var t = Tpl();
        var sends = Enumerable.Range(0, 3).Select(i => Send(t, "דני", "היי {n}!\nשורה א  \n\nשורה ב\nשורה ג", i)).ToList();
        Assert.Empty(TemplateLearning.EditProposals(new[] { t }, sends, new HashSet<string>()));
    }

    // ---- clustering ------------------------------------------------------------------

    [Fact]
    public void RepeatedFreeText_ClustersIntoNewTemplateProposal()
    {
        var sends = new List<TemplateSend>
        {
            Free("היי דני, שולח לך את הקישור לפגישה מחר בעשר", "דני", 1),
            Free("היי משה, שולח לך את הקישור לפגישה מחר בעשר", "משה", 2),
            Free("היי רינה, שולח לך את הקישור לפגישה מחר", "רינה", 3),
            Free("מה שלומך? מתי נוח לדבר על ההפקדה", null, 4),
        };
        var clusters = TemplateLearning.Clusters(sends);
        var c = Assert.Single(clusters);
        Assert.Equal(3, c.Count);

        var p = Assert.Single(TemplateLearning.NewTemplateProposals(sends, new HashSet<string>()));
        Assert.Equal(ProposalKind.NewTemplate, p.Kind);
        Assert.DoesNotContain("דני", p.After);
        Assert.DoesNotContain("משה", p.After);
        Assert.Contains("הקישור לפגישה", p.After);
        Assert.Empty(TemplateLearning.NewTemplateProposals(sends, new HashSet<string> { p.Key }));
    }

    [Fact]
    public void TwoSimilarFreeTexts_AreNotEnough()
    {
        var sends = new List<TemplateSend>
        {
            Free("שולח לך את הקישור לפגישה", null, 1),
            Free("שולח לך את הקישור לפגישה", null, 2),
        };
        Assert.Empty(TemplateLearning.Clusters(sends));
    }

    [Fact]
    public void DiffLines_MarksRemovedAndAdded()
    {
        var lines = TemplateLearning.DiffLines("a\nb\nc", "a\nB\nc");
        Assert.Equal(4, lines.Count);
        Assert.Contains((TemplateLearning.LineOp.Removed, "b"), lines);
        Assert.Contains((TemplateLearning.LineOp.Added, "B"), lines);
        Assert.Equal((TemplateLearning.LineOp.Same, "a"), lines[0]);
        Assert.Equal((TemplateLearning.LineOp.Same, "c"), lines[3]);
    }
}
