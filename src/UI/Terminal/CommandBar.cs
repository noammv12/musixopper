using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Palon.Agent;
using Palon.Agentic;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// The Now window's command bar (Ctrl+K). Typing asks the agentic layer's
/// CommandRouter for completions and a previewable action — nothing runs
/// until the rep confirms the preview card. "תב" opens the template palette
/// (↑/↓/Enter). 1–5 on an empty bar copies a pinned template. Anything the
/// router can't preview goes to Palon's agent loop, answered inline with a
/// typewriter while Palon talks.
/// </summary>
sealed class CommandBar : StackPanel
{
    readonly TerminalWindow _host;
    readonly NowScreen _now;
    readonly TextBox _box;
    readonly Border _frame;
    readonly Border _panel;
    readonly StackPanel _panelBody = new();
    readonly WrapPanel _below = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(260) };
    readonly DispatcherTimer _type = new() { Interval = TimeSpan.FromMilliseconds(16) };

    static AssistantSession? _session;

    List<PaletteItem> _palette = new();
    int _sel;
    CommandPreview? _preview;
    string _previewFor = "";
    CancellationTokenSource? _previewCts;
    CancellationTokenSource? _runCts;
    int _generation;

    // typewriter
    TextBlock? _answer;
    string _full = "";
    int _shown;

    public CommandBar(TerminalWindow host, NowScreen now)
    {
        _host = host;
        _now = now;
        MaxWidth = 700;

        _panel = Fx.Glass2(24, new Thickness(18, 16, 18, 16));
        _panel.Margin = new Thickness(0, 0, 0, 10);
        _panel.Visibility = Visibility.Collapsed;
        _panel.Child = _panelBody;
        Children.Add(_panel);

        var (inputHost, box) = Kit.Input("תגיד ל-Palon מה לעשות… או הקלד תב לתבניות", 16.5);
        _box = box;
        System.Windows.Automation.AutomationProperties.SetName(_box, "פקודה ל-Palon");
        var spark = Kit.Icon(Icons.Search, 18, Tone.MutedSoft);
        spark.Margin = new Thickness(0, 0, 12, 0);
        spark.VerticalAlignment = VerticalAlignment.Center;
        var run = Kit.IconButton(Icons.Send, 40, Run, PillKind.Ghost, 18, Tone.OnPrimary);
        run.Background = Tone.Primary;
        run.ToolTip = "בצע · Enter";
        Kit.Press(run, 0.9);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(spark);
        Grid.SetColumn(inputHost, 1);
        row.Children.Add(inputHost);
        Grid.SetColumn(run, 2);
        row.Children.Add(run);
        _frame = new Border
        {
            Height = 60, CornerRadius = new CornerRadius(30), Background = Fx.Vertical("#E62A2B30", "#EB151518"),
            BorderBrush = Tone.GlassDeepRim, BorderThickness = new Thickness(1), Padding = new Thickness(22, 0, 10, 0), Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 50, ShadowDepth = 16, Direction = 270, Opacity = 0.6, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
        };
        _frame.MouseLeftButtonDown += (_, _) => _box.Focus();
        _box.GotKeyboardFocus += (_, _) => _frame.BorderBrush = Tone.AccentLine;
        _box.LostKeyboardFocus += (_, _) => _frame.BorderBrush = Tone.GlassDeepRim;
        Children.Add(_frame);
        Children.Add(_below);

        _box.TextChanged += (_, _) => OnText();
        _box.PreviewKeyDown += OnKey;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = FetchPreview(_box.Text.Trim());
        };
        _type.Tick += (_, _) => TypeTick();
        Unloaded += (_, _) => Cancel();
        RenderBelow();
    }

    public void FocusInput()
    {
        _box.Focus();
        _box.SelectAll();
    }

    public bool HasText => _box.Text.Length > 0;

    /// <summary>Puts text in the bar (a quick action) with the caret at the end.</summary>
    public void Prefill(string text)
    {
        _box.Text = text;
        _box.Focus();
        _box.CaretIndex = text.Length;
    }

    public void Clear()
    {
        Cancel();
        _box.Text = "";
        HidePanel();
    }

    public void Cancel()
    {
        _debounce.Stop();
        _previewCts?.Cancel();
        _runCts?.Cancel();
        _type.Stop();
        _generation++;
    }

    // ---- typing -------------------------------------------------------------------

    void OnText()
    {
        var text = _box.Text;
        _preview = null;
        _previewCts?.Cancel();
        _debounce.Stop();
        if (text.Trim().Length == 0)
        {
            _now.BarMood(null);
            HidePanel();
            RenderBelow();
            return;
        }
        _now.BarMood(PalonMood.Listen);
        if (CommandText.IsPalette(text))
        {
            _palette = CommandText.Palette(TemplatesStore.Load(), SnippetStore.Load(), CommandText.PaletteQuery(text), _now.ActiveFirstName);
            _sel = 0;
            RenderPalette();
            RenderBelow();
            return;
        }
        HidePanel();
        RenderBelow();
        _debounce.Start();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var palette = CommandText.IsPalette(_box.Text) && _panel.Visibility == Visibility.Visible;
        switch (e.Key)
        {
            case Key.Down when palette:
                _sel = CommandText.Move(_sel, 1, _palette.Count);
                RenderPalette();
                e.Handled = true;
                return;
            case Key.Up when palette:
                _sel = CommandText.Move(_sel, -1, _palette.Count);
                RenderPalette();
                e.Handled = true;
                return;
            case Key.Enter:
                Run();
                e.Handled = true;
                return;
            case Key.Escape when _box.Text.Length > 0 || _panel.Visibility == Visibility.Visible:
                Clear();
                e.Handled = true;
                return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && DigitOf(e.Key) is int digit
            && CommandText.PinnedIndex(_box.Text, digit, TemplatesStore.Load().Count) is int index)
        {
            _now.CopyPinned(index);
            e.Handled = true;
        }
    }

    public static int? DigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    // ---- the suggestions row under the bar ----------------------------------------

    void RenderBelow()
    {
        _below.Children.Clear();
        var text = _box.Text.Trim();
        if (CommandText.IsPalette(text)) return;
        IReadOnlyList<string> suggestions;
        try
        {
            suggestions = CommandRouter.Suggest(text);
        }
        catch (Exception ex)
        {
            Log.Write($"Command suggest failed: {ex.Message}");
            suggestions = Array.Empty<string>();
        }
        if (text.Length == 0)
        {
            var hint = Kit.T("1–5 מעתיק תבנית · תב לכל התבניות", 12.5, Tone.Faint);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(0, 0, 12, 0);
            _below.Children.Add(hint);
            if (suggestions.Count == 0) suggestions = new[] { "מה הקצב שלי?", "למי עוד לא חזרתי היום?", "סכם לי את השבוע למנהל" };
        }
        foreach (var s in suggestions.Take(4))
        {
            var chip = Kit.Pill(s, PillKind.Ghost, () =>
            {
                Prefill(s);
                if (text.Length == 0) Run();
            }, height: 30, fontSize: 12.5);
            chip.Margin = new Thickness(0, 0, 6, 0);
            _below.Children.Add(chip);
        }
    }

    // ---- panel ------------------------------------------------------------------------

    void ShowPanel()
    {
        if (_panel.Visibility == Visibility.Visible) return;
        _panel.Visibility = Visibility.Visible;
        if (!Fx.Allowed(_host)) return;
        var move = new TranslateTransform(0, 10);
        _panel.RenderTransform = move;
        _panel.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 260));
        move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(10, 0, 420));
    }

    void HidePanel()
    {
        _panel.Visibility = Visibility.Collapsed;
        _panelBody.Children.Clear();
        _answer = null;
    }

    void RenderPalette()
    {
        _panelBody.Children.Clear();
        var name = _now.ActiveFirstName;
        var head = Kit.Bar(Fx.Kicker("תבניות ומשפטים" + (name is { Length: > 0 } ? $" · ימולא: {name}" : "")),
            Kit.T("↑ ↓ לבחירה · Enter מעתיק", 12, Tone.Faint));
        head.Margin = new Thickness(4, 0, 4, 8);
        _panelBody.Children.Add(head);
        if (_palette.Count == 0)
        {
            _panelBody.Children.Add(Kit.T("אין תבנית כזו. נסה ״הפקדה״ או ״שאלון״.", 13.5, Tone.Muted));
            ShowPanel();
            return;
        }
        var list = new StackPanel();
        for (var i = 0; i < _palette.Count && i < 8; i++)
        {
            var item = _palette[i];
            var index = i;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var key = Kit.Num(item.Key, 12.5, Tone.MutedSoft, FontWeights.Medium);
            key.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(key);
            var title = Kit.T(item.Title, 14.5, Tone.Text, FontWeights.Medium);
            title.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(title, 1);
            g.Children.Add(title);
            var sub = Kit.Auto(Kit.T(item.Sub, 12.5, Tone.Muted));
            sub.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sub, 2);
            g.Children.Add(sub);
            if (i == _sel)
            {
                var enter = Kit.T("Enter", 11.5, Tone.MutedSoft);
                enter.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(enter, 3);
                g.Children.Add(enter);
            }
            var row = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 9, 12, 9), Child = g, Cursor = Cursors.Hand,
                Background = i == _sel ? Tone.FillSelected : Brushes.Transparent,
            };
            Kit.Clickable(row, () =>
            {
                _sel = index;
                CopySelected();
            });
            list.Children.Add(row);
        }
        _panelBody.Children.Add(list);
        ShowPanel();
    }

    void CopySelected()
    {
        if (_sel < 0 || _sel >= _palette.Count) return;
        var item = _palette[_sel];
        _now.CopyPaletteItem(item);
        Clear();
    }

    Border Working(string label)
    {
        var dot = new PalonAvatar { Width = 30, Height = 30, Mood = PalonMood.Think, FollowPointer = false };
        var t = Kit.T(label, 14, Tone.Body);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(10, 0, 0, 0);
        return new Border { Child = Kit.Row(0, dot, t) };
    }

    // ---- run --------------------------------------------------------------------------

    void Run()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            _box.Focus();
            return;
        }
        if (CommandText.IsPalette(text))
        {
            CopySelected();
            return;
        }
        if (_preview is { } p && _previewFor == text)
        {
            _ = Confirm(p);
            return;
        }
        _ = RunText(text);
    }

    async Task RunText(string text)
    {
        _debounce.Stop();
        var preview = await FetchPreview(text);
        if (preview is null && _box.Text.Trim() == text) _ = AskPalon(text);
    }

    async Task<CommandPreview?> FetchPreview(string text)
    {
        if (text.Length == 0 || CommandText.IsPalette(text)) return null;
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _previewCts = cts;
        CommandPreview? preview;
        try
        {
            preview = await CommandRouter.PreviewAsync(text, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Command preview failed: {ex.Message}");
            return null;
        }
        if (cts.IsCancellationRequested || _box.Text.Trim() != text) return null;
        _preview = preview;
        _previewFor = text;
        if (preview is not null) RenderPreview(preview);
        return preview;
    }

    void RenderPreview(CommandPreview p)
    {
        _panelBody.Children.Clear();
        var col = new StackPanel();
        col.Children.Add(Fx.Kicker(p.Kind));
        var title = Kit.T(p.Title, 17, Tone.Text, FontWeights.SemiBold, wrap: true);
        title.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(title);
        if (p.Detail.Length > 0)
        {
            var detail = Kit.T(p.Detail, 13.5, Tone.Body, wrap: true);
            detail.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(detail);
        }
        var go = Kit.Pill(p.ConfirmLabel, PillKind.Primary, () => _ = Confirm(p), height: 40, fontSize: 14);
        go.ToolTip = "Enter";
        var bar = Kit.Bar(col, go);
        _panelBody.Children.Add(bar);
        ShowPanel();
        _now.Say(p.Title, quiet: true);
    }

    async Task Confirm(CommandPreview p)
    {
        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        var gen = ++_generation;
        _panelBody.Children.Clear();
        _panelBody.Children.Add(Working("מבצע…"));
        ShowPanel();
        _now.BarMood(PalonMood.Think);
        string? result;
        try
        {
            result = await p.Execute(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"Command execute failed: {ex}");
            if (gen != _generation) return;
            _now.BarMood(null);
            _panelBody.Children.Clear();
            _panelBody.Children.Add(Kit.T("זה לא הצליח — ראה לוג.", 14, Tone.RedText));
            return;
        }
        if (gen != _generation) return;
        _box.Text = "";
        HidePanel();
        _now.BarMood(null);
        _host.Toast(result ?? "בוצע");
        _now.Cheer();
        _host.Refresh();
    }

    // ---- ask Palon (agent loop, inline) ------------------------------------------------

    async Task AskPalon(string question)
    {
        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        var gen = ++_generation;
        _panelBody.Children.Clear();
        var working = Working("מבין מה ביקשת…");
        _panelBody.Children.Add(working);
        ShowPanel();
        _now.BarMood(PalonMood.Think);
        if (!AiChat.HasKey)
        {
            ShowAnswer("כדי לשאול שאלות חופשיות צריך מפתח Gemini או DeepSeek (במוח של Palon). התבניות והכלים עובדים גם בלי.", gen);
            return;
        }
        var streamed = false;
        void OnText(string sentence) => Dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            if (!streamed)
            {
                streamed = true;
                ShowAnswer(sentence, gen);
                return;
            }
            _full += " " + sentence;
            if (!_type.IsEnabled) _type.Start();
        });
        var steps = new Progress<AgentStep>(step =>
        {
            if (gen != _generation || streamed) return;
            if (step.State == AgentStepState.Started && working.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock t)
                t.Text = step.Label + "…";
        });
        _session ??= new AssistantSession(_host.CallStateSource, _host.CurrentNumberSource);
        string answer;
        AgentOutcome? outcome = null;
        try
        {
            var result = await AgentLoop.RunAsync(_session, question, cts.Token,
                new AgentRunOptions { Steps = steps, Approver = new BarApprover(this, gen), OnText = OnText });
            if (result.Failure == AgentFailure.Cancelled || cts.IsCancellationRequested) return;
            outcome = result.Outcome;
            answer = outcome?.Text ?? (result.Failure == AgentFailure.ProviderDown
                ? "Palon לא מצליח להגיע למודל כרגע — נסה שוב עוד רגע."
                : "לא הצלחתי למצוא תשובה. נסה לנסח אחרת.");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"Command bar ask failed: {ex}");
            answer = "משהו השתבש — ראה לוג.";
        }
        if (gen != _generation) return;
        if (streamed && outcome is { Acted: false }) _full = answer;
        else ShowAnswer(answer, gen);
        if (!_type.IsEnabled && _shown < _full.Length) _type.Start();
        if (outcome?.Acted == true) _host.Refresh();
        if (outcome?.Undo is { } undo)
            _host.Toast("בוצע", "בטל", async () =>
            {
                try
                {
                    _host.Toast(await undo());
                }
                catch (Exception ex)
                {
                    Log.Write($"Command bar undo failed: {ex.Message}");
                    _host.Toast("הביטול לא הצליח — ראה לוג");
                }
            });
    }

    void ShowAnswer(string text, int gen)
    {
        if (gen != _generation) return;
        _panelBody.Children.Clear();
        _full = text;
        _shown = 0;
        var avatar = new PalonAvatar { Width = 30, Height = 30, Mood = PalonMood.Talk, FollowPointer = false, VerticalAlignment = VerticalAlignment.Top };
        _answer = new TextBlock
        {
            FontSize = 15, FontFamily = Font.Family, Foreground = Tone.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 25,
            Margin = new Thickness(12, 2, 0, 0),
            FlowDirection = Bidi.HasRtl(text) || text.Length == 0 ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(avatar);
        Grid.SetColumn(_answer, 1);
        g.Children.Add(_answer);
        var scroll = new ScrollViewer { Content = g, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Ui.ThinScroll(scroll);
        _panelBody.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(42, 10, 0, 0) };
        actions.Children.Add(Kit.Pill("העתק תשובה", PillKind.Secondary, () => _host.CopyWithToast(_full, "התשובה הועתקה"), height: 30, fontSize: 12.5));
        var close = Kit.Pill("סגור", PillKind.Ghost, Clear, height: 30, fontSize: 12.5);
        close.Margin = new Thickness(6, 0, 0, 0);
        actions.Children.Add(close);
        _panelBody.Children.Add(actions);
        ShowPanel();
        _now.BarMood(PalonMood.Talk);
        if (Fx.Reduced) _shown = _full.Length;
        _type.Start();
        TypeTick();
    }

    void TypeTick()
    {
        if (_answer is null)
        {
            _type.Stop();
            return;
        }
        _shown = Math.Min(_full.Length, _shown + 3);
        _answer.Text = _full[.._shown] + (_shown < _full.Length ? "▍" : "");
        if (_shown < _full.Length) return;
        _type.Stop();
        _now.BarMood(null);
        _now.Cheer(soft: true);
    }

    /// <summary>External tools and plans ask here, inline in the panel.</summary>
    sealed class BarApprover : IAgentApprover
    {
        readonly CommandBar _bar;
        readonly int _gen;

        public BarApprover(CommandBar bar, int gen)
        {
            _bar = bar;
            _gen = gen;
        }

        public Task<bool> ApproveToolAsync(AgentTool tool, string argumentsJson, CancellationToken ct) =>
            Ask("לאשר את הפעולה?", $"{ToolLabels.For(tool.Name)}\n{argumentsJson}", ct);

        public Task<bool> ApprovePlanAsync(string plan, CancellationToken ct) => Ask("זו התוכנית. לבצע?", plan, ct);

        Task<bool> Ask(string title, string body, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetResult(false));
            _bar.Dispatcher.BeginInvoke(() =>
            {
                if (_gen != _bar._generation) { tcs.TrySetResult(false); return; }
                var col = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                col.Children.Add(Kit.T(title, 14, Tone.Text, FontWeights.SemiBold));
                col.Children.Add(Kit.T(body, 13, Tone.Body, wrap: true));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                Border? card = null;
                void Done(bool ok)
                {
                    tcs.TrySetResult(ok);
                    if (card is not null) _bar._panelBody.Children.Remove(card);
                }
                row.Children.Add(Kit.Pill("אשר", PillKind.Primary, () => Done(true), height: 34, fontSize: 13));
                var no = Kit.Pill("לא", PillKind.Ghost, () => Done(false), height: 34, fontSize: 13);
                no.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(no);
                col.Children.Add(row);
                card = Kit.ListRow(col, new Thickness(12, 10, 12, 10));
                _bar._panelBody.Children.Add(card);
                _bar.ShowPanel();
            });
            return tcs.Task;
        }
    }
}
