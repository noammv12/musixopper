using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>The quick-access shelf: pinned templates 1–5, snippets, quick actions, recent clients.</summary>
sealed partial class NowScreen
{
    string _shelfSig = "";
    readonly Dictionary<string, (TextBlock Label, string Idle)> _chipLabels = new();
    readonly DispatcherTimer _chipRevert = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    string? _copiedKey;
    Popup? _popup;

    void RenderShelf(TermData d, List<MessageTemplate> templates)
    {
        var snippets = SnippetStore.Load();
        var name = ActiveFirstName;
        var recent = RecentClients(d);
        var sig = string.Join("|", templates.Take(CommandText.Pinned).Select(t => t.Id + t.Version + t.Title))
                  + "#" + string.Join("|", snippets.Take(3).Select(s => s.Label + s.Text.Length))
                  + "#" + name + "#" + string.Join("|", recent.Select(r => r.Key + r.Callbacks.Count + r.Calls.Count));
        if (sig == _shelfSig && _shelfHost.Child is not null) return;
        _shelfSig = sig;
        _chipLabels.Clear();
        _popup = null;

        var shelf = Kit.Glass(26, new Thickness(18, 16, 18, 14));
        var col = new StackPanel();

        // Templates 1–5.
        var tplRow = new WrapPanel();
        for (var i = 0; i < templates.Count && i < CommandText.Pinned; i++)
        {
            var t = templates[i];
            var index = i;
            var preview = TemplateFill.Fill(t, name);
            tplRow.Children.Add(Chip("t" + i, (i + 1).ToString(), ShortTitle(t.Title), $"העתק {t.Title}",
                () => CopyPinned(index),
                () => PreviewCard(t.Title, name is null ? null : $"ימולא: {name}", preview, () => CopyPinned(index), () => EditBeforeCopy(t.Title, preview, t))));
        }
        col.Children.Add(ShelfRow("תבניות", tplRow));

        // Snippets.
        if (snippets.Count > 0)
        {
            var snipRow = new WrapPanel();
            for (var i = 0; i < snippets.Count && i < 3; i++)
            {
                var s = snippets[i];
                snipRow.Children.Add(Chip("s" + i, "“", s.Label, $"העתק {s.Label}", () => CopySnippet(s, "s" + snippets.IndexOf(s)),
                    () => PreviewCard(s.Label, null, s.Text, () => CopySnippet(s, "s" + snippets.IndexOf(s)), () => EditBeforeCopy(s.Label, s.Text, null))));
            }
            var row = ShelfRow("משפטים", snipRow);
            row.Margin = new Thickness(0, 8, 0, 0);
            col.Children.Add(row);
        }

        col.Children.Add(new Border { Height = 1, Background = Tone.Hairline, Margin = new Thickness(0, 12, 0, 12) });

        // Quick actions + recent clients.
        var acts = new WrapPanel();
        void Act(string label, string icon, Action run)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 7, 12, 7), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
                Child = Kit.Row(7, Kit.Icon(icon, 15, Tone.TextSoft), Kit.T(label, 12.5, Tone.TextSoft, FontWeights.Medium)),
                Focusable = true, FocusVisualStyle = null,
            };
            Kit.HoverFill(b, Tone.FillSoft, Tone.FillHover);
            Kit.Press(b, 0.94);
            Kit.Clickable(b, run);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            acts.Children.Add(b);
        }
        Act("חזרה חדשה", NowIcons.Cal, () => _bar.Prefill((ActiveFirstName is { } n ? n + " " : "") + "מחר ב-"));
        Act("עסקה חדשה", NowIcons.Deal, () => Sheets.NewDeal(Host));
        Act("קרא מסך", NowIcons.Eye, TerminalWindow.ReadScreenFromShortcut);
        Act("קבלה ← עסקה", NowIcons.Receipt, () => Host.ReadReceipt());
        Act("רשום ב-Salesforce", NowIcons.Cloud, () =>
        {
            if (LastNote(d) is { } note) SalesforceSheets.LogCall(Host, note, null);
            else Host.Toast("אין עדיין שיחה לתעד");
        });
        Act("העתק סיכום אחרון", NowIcons.Copy, () =>
        {
            if (LastNote(d) is not { } note)
            {
                Host.Toast("אין עדיין שיחה עם סיכום");
                return;
            }
            var who = ClientIndex.NameForPhone(d.Callbacks, note.Number);
            Host.CopyWithToast(CallSummary.Format(note, who, note.StartedUtc.ToLocalTime()), $"הסיכום של {who ?? note.Number ?? "השיחה האחרונה"} הועתק");
        });

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(acts);
        if (recent.Count > 0)
        {
            var people = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var lab = Kit.T("אחרונים", 12, Tone.Faint);
            lab.VerticalAlignment = VerticalAlignment.Center;
            lab.Margin = new Thickness(8, 0, 8, 0);
            people.Children.Add(lab);
            foreach (var r in recent) people.Children.Add(Avatar(r, d));
            Grid.SetColumn(people, 1);
            bottom.Children.Add(people);
        }
        col.Children.Add(bottom);
        shelf.Child = col;
        shelf.MouseEnter += (_, _) => _hero.Gaze = new Point(-0.4, 0.8);
        shelf.MouseLeave += (_, _) => _hero.Gaze = null;
        _shelfHost.Child = shelf;
    }

    static CallNote? LastNote(TermData d) =>
        d.Notes.Where(n => !string.IsNullOrWhiteSpace(n.Summary)).OrderByDescending(n => n.StartedUtc).FirstOrDefault();

    static string ShortTitle(string title)
    {
        var dot = title.IndexOf(" · ", StringComparison.Ordinal);
        var s = dot > 0 ? title[..dot] : title;
        return s.Length > 14 ? s[..14] + "…" : s;
    }

    static FrameworkElement ShelfRow(string label, UIElement items)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = Kit.T(label, 12, Tone.Faint);
        t.VerticalAlignment = VerticalAlignment.Top;
        t.Margin = new Thickness(0, 8, 0, 0);
        g.Children.Add(t);
        Grid.SetColumn(items, 1);
        g.Children.Add(items);
        return g;
    }

    /// <summary>A copy chip: key + label; a check with the filled name for 1.8s after a copy;
    /// hover (or keyboard focus) shows the preview with copy / edit-before-copy.</summary>
    FrameworkElement Chip(string key, string keyLabel, string label, string aria, Action copy, Func<FrameworkElement> preview)
    {
        var k = Kit.Num(keyLabel, 11.5, Tone.Faint, FontWeights.Medium);
        k.VerticalAlignment = VerticalAlignment.Center;
        var text = Kit.T(label, 13, Tone.Text, FontWeights.Medium);
        text.VerticalAlignment = VerticalAlignment.Center;
        _chipLabels[key] = (text, label);
        var chip = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(12, 0, 14, 0), Margin = new Thickness(0, 0, 6, 6),
            BorderBrush = Tone.B("#14FFFFFF"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
            Child = Kit.Row(7, k, text),
        };
        Kit.HoverFill(chip, Tone.B("#0DFFFFFF"), Tone.FillHover);
        Kit.Press(chip, 0.94);
        Kit.Magnify(chip, 1.03, -2);
        Kit.Clickable(chip, copy);
        System.Windows.Automation.AutomationProperties.SetName(chip, aria);

        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            OpenPreview(chip, preview());
        };
        chip.MouseEnter += (_, _) => hover.Start();
        chip.MouseLeave += (_, _) =>
        {
            hover.Stop();
            ClosePreviewSoon();
        };
        chip.GotKeyboardFocus += (_, _) => OpenPreview(chip, preview());
        return chip;
    }

    void OpenPreview(FrameworkElement target, FrameworkElement content)
    {
        if (_popup is { } old) old.IsOpen = false;
        var card = Fx.Glass2(20, new Thickness(16, 14, 16, 14));
        card.Width = 360;
        card.FlowDirection = FlowDirection.RightToLeft;
        card.Child = content;
        var popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = true,
            PopupAnimation = Fx.Reduced ? PopupAnimation.None : PopupAnimation.Fade, Child = new Border { Padding = new Thickness(0, 0, 0, 8), Child = card, Background = Brushes.Transparent },
            VerticalOffset = -2,
        };
        popup.MouseEnter += (_, _) => _popupClose?.Stop();
        popup.MouseLeave += (_, _) => ClosePreviewSoon();
        _popup = popup;
        popup.IsOpen = true;
    }

    DispatcherTimer? _popupClose;

    void ClosePreviewSoon()
    {
        _popupClose ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _popupClose.Stop();
        _popupClose.Tick -= ClosePreview;
        _popupClose.Tick += ClosePreview;
        _popupClose.Start();
    }

    void ClosePreview(object? sender, EventArgs e)
    {
        _popupClose?.Stop();
        if (_popup is { } p) p.IsOpen = false;
        _popup = null;
    }

    FrameworkElement PreviewCard(string title, string? fillLine, string text, Action copy, Action edit)
    {
        var col = new StackPanel();
        var head = Kit.Bar(Kit.T(title, 14, Tone.Text, FontWeights.SemiBold), Kit.T(fillLine ?? "", 12, Tone.MutedSoft));
        col.Children.Add(head);
        var body = Kit.T(text.Length > 420 ? text[..420] + "…" : text, 12.5, Tone.Body, wrap: true);
        body.LineHeight = 19;
        body.Margin = new Thickness(0, 8, 0, 0);
        body.FlowDirection = Bidi.HasRtl(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        col.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Kit.Pill("העתק", PillKind.Primary, () => { ClosePreview(null, EventArgs.Empty); copy(); }, height: 30, fontSize: 12.5));
        var e = Kit.Pill("ערוך לפני העתקה", PillKind.Ghost, () => { ClosePreview(null, EventArgs.Empty); edit(); }, height: 30, fontSize: 12.5);
        e.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(e);
        col.Children.Add(row);
        return col;
    }

    /// <summary>A quick edit sheet: tweak the filled text, then copy it (the send is logged with the edit,
    /// so template learning sees what the rep changed).</summary>
    void EditBeforeCopy(string title, string text, MessageTemplate? template)
    {
        var body = new StackPanel();
        body.Children.Add(Kit.T(title, Display.Sheet, Tone.Text, FontWeights.SemiBold));
        var (frame, box) = Kit.Field("", 14.5, 260, 18);
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        box.Text = text;
        frame.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(frame);
        Action close = () => { };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(Kit.Pill("העתק", PillKind.Primary, () =>
        {
            Host.CopyWithToast(box.Text, $"{title} הועתקה");
            if (template is not null) TemplateLearningStore.LogCopy(template, _cards.FirstOrDefault()?.Who, null, text, box.Text == text ? null : box.Text, "now-edit");
            close();
        }, height: 40));
        var cancel = Kit.Pill("ביטול", PillKind.Ghost, () => close(), height: 40);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(cancel);
        body.Children.Add(row);
        close = Host.ShowSheet(body, 560);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
    }

    // ---- copying -------------------------------------------------------------------------

    /// <summary>Copies pinned template <paramref name="index"/> (0-based) with the active client's name.</summary>
    public void CopyPinned(int index)
    {
        var templates = TemplatesStore.Load();
        if (index < 0 || index >= templates.Count) return;
        CopyTemplate(templates[index], _cards.FirstOrDefault()?.Who, "now-shelf", chipKey: "t" + index);
    }

    public void CopyTemplate(MessageTemplate t, string? who, string source, string? phone = null, string? chipKey = null)
    {
        var first = who is null || who.Any(char.IsDigit) ? "" : TemplateFill.FirstName(who);
        var filled = TemplateFill.Fill(t, first);
        Kit.Copy(filled);
        TemplateLearningStore.LogCopy(t, who, phone, filled, null, source);
        Host.Toast(first.Length > 0 ? $"הועתק · {t.Title} · {first}" : $"הועתק · {t.Title}");
        FlashChip(chipKey ?? "t" + TemplatesStore.Load().FindIndex(x => x.Id == t.Id), first.Length > 0 ? first : "הועתק");
        _hero.Gaze = new Point(-0.4, 0.8);
        Cheer(soft: true);
    }

    void CopySnippet(Snippet s, string chipKey)
    {
        Kit.Copy(s.Text);
        Host.Toast($"הועתק · {s.Label}");
        FlashChip(chipKey, "הועתק");
    }

    public void CopyPaletteItem(PaletteItem item)
    {
        if (!item.IsSnippet && item.TemplateId is { } id && TemplatesStore.Load().FirstOrDefault(t => t.Id == id) is { } t)
        {
            CopyTemplate(t, _cards.FirstOrDefault()?.Who, "now-palette");
            return;
        }
        Kit.Copy(item.Text);
        Host.Toast($"הועתק · {item.Title}");
    }

    void FlashChip(string key, string label)
    {
        if (_copiedKey is { } prev && _chipLabels.TryGetValue(prev, out var p)) p.Label.Text = p.Idle;
        if (!_chipLabels.TryGetValue(key, out var chip)) return;
        _copiedKey = key;
        chip.Label.Text = "✓ " + label;
        if (chip.Label.Parent is FrameworkElement fe && fe.Parent is FrameworkElement border) Kit.Pop(border);
        _chipRevert.Stop();
        _chipRevert.Tick -= RevertChip;
        _chipRevert.Tick += RevertChip;
        _chipRevert.Start();
    }

    void RevertChip(object? sender, EventArgs e)
    {
        _chipRevert.Stop();
        if (_copiedKey is { } k && _chipLabels.TryGetValue(k, out var c)) c.Label.Text = c.Idle;
        _copiedKey = null;
    }

    // ---- recent clients ------------------------------------------------------------------

    static List<ClientCard> RecentClients(TermData d) =>
        ClientIndex.Build(d.Notes, d.Callbacks, d.Book?.Deals ?? new List<Sales.Deal>())
            .Where(c => c.Calls.Count > 0 || c.Callbacks.Count > 0)
            .Take(3).ToList();

    FrameworkElement Avatar(ClientCard c, TermData d)
    {
        var dot = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand,
            Background = Fx.Vertical("#33FFFFFF", "#14FFFFFF"), BorderBrush = Tone.B("#26FFFFFF"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = c.Initials, FontFamily = Font.Family, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Tone.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Focusable = true, FocusVisualStyle = null, ToolTip = c.Name,
        };
        Kit.Press(dot, 0.9);
        Kit.Magnify(dot, 1.1, -2);
        System.Windows.Automation.AutomationProperties.SetName(dot, c.Name);
        Kit.Clickable(dot, () => OpenPreview(dot, Peek(c, d)));
        return dot;
    }

    FrameworkElement Peek(ClientCard c, TermData d)
    {
        var col = new StackPanel();
        var status = c.Tone(d.Now) switch
        {
            ClientTone.Overdue => ("באיחור", Tone.B("#FFF2C48D")),
            ClientTone.Deposited => ("הפקיד", Tone.B("#FFA6D6BE")),
            ClientTone.Due => ("חזרה קרובה", Tone.B("#FFB9CCEA")),
            _ => ("בתהליך", Tone.B("#FFB9CCEA")),
        };
        var head = new StackPanel();
        head.Children.Add(Kit.T(c.Name, 16, Tone.Text, FontWeights.SemiBold));
        if (c.Meta.Length > 0) head.Children.Add(Kit.T(c.Meta, 12, Tone.Muted));
        col.Children.Add(Kit.Bar(head, Kit.T(status.Item1, 12, status.Item2, FontWeights.Medium)));
        var timeline = c.Calls.OrderByDescending(n => n.StartedUtc).Take(2).ToList();
        foreach (var n in timeline)
        {
            var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(Kit.Mono(He.Ago(n.StartedUtc.ToLocalTime(), d.Now), 11.5, Tone.Faint));
            row.Children.Add(Kit.T(CallSummary.Line(n), 12.5, Tone.Body, wrap: true));
            col.Children.Add(row);
        }
        if (c.NextCallback is { } next)
        {
            var t = Kit.T($"הבא: {He.When(next.DueAtUtc.ToLocalTime(), d.Now)} · {next.DisplayLine}", 12.5, Tone.TextSoft);
            t.Margin = new Thickness(0, 10, 0, 0);
            col.Children.Add(Kit.Auto(t));
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var templates = TemplatesStore.Load();
        var suggest = TemplateFill.Suggest(templates, string.Join("\n", c.Calls.Select(n => n.Summary ?? ""))) ?? templates.FirstOrDefault();
        if (suggest is not null)
            buttons.Children.Add(Kit.Pill($"העתק {ShortTitle(suggest.Title)}", PillKind.Primary, () =>
            {
                ClosePreview(null, EventArgs.Empty);
                CopyTemplate(suggest, c.Name, "now-peek", c.Phone);
            }, height: 30, fontSize: 12.5));
        var open = Kit.Pill("בלקוחות", PillKind.Secondary, () =>
        {
            ClosePreview(null, EventArgs.Empty);
            Host.Navigate(TerminalPage.Clients);
        }, height: 30, fontSize: 12.5);
        open.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(open);
        var close = Kit.Pill("סגור", PillKind.Ghost, () => ClosePreview(null, EventArgs.Empty), height: 30, fontSize: 12.5);
        close.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(close);
        col.Children.Add(buttons);
        return col;
    }
}
