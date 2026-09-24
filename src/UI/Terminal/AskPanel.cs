using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Palon.Agent;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Ask Palon, typed. Palon listens while you type, thinks while the agent
/// works (each tool it reaches for shows up as a step), talks while the
/// answer types itself out, then does a small happy hop. The four
/// suggestions about the month are answered locally from the sales data —
/// instant and exact; anything else goes through the agent loop, which now
/// also sees the month's numbers.
/// </summary>
sealed class AskPanel : Border
{
    static AssistantSession? _session;

    readonly TerminalWindow _host;
    readonly PalonAvatar _avatar = new() { Width = 170, Height = 170, Mood = PalonMood.Listen };
    readonly TextBlock _status;
    readonly TextBox _input;
    readonly Grid _suggestions = new();
    readonly StackPanel _busy = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _question;
    readonly StackPanel _steps = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly Border _answerCard;
    readonly TextBlock _answer;
    readonly Border _caret;
    readonly DispatcherTimer _type = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly DispatcherTimer _settle = new();
    CancellationTokenSource? _cts;
    string _full = "";
    int _shown;
    int _generation;

    public AskPanel(TerminalWindow host)
    {
        _host = host;
        Width = 740;
        CornerRadius = new CornerRadius(36);
        Background = Tone.GlassDeep;
        BorderBrush = Tone.GlassDeepRim;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(34, 30, 34, 28);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 80, ShadowDepth = 24, Direction = 270, Opacity = 0.7, Color = System.Windows.Media.Colors.Black };

        var root = new StackPanel();
        var close = Kit.IconButton(Icons.Close, 34, () => _host.CloseOverlay());
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Margin = new Thickness(0, -10, -12, -40);
        root.Children.Add(close);
        var model = ModelPicker.Pill(host, compact: false);
        model.HorizontalAlignment = HorizontalAlignment.Right;
        model.Margin = new Thickness(-8, -8, 0, -30);
        root.Children.Add(model);

        _avatar.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(_avatar);
        _status = Kit.T("אני מקשיב. מה תרצה לדעת?", 14.5, Tone.Body);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(_status);

        var (inputHost, input) = Kit.Input("שאל על החודש, לקוח, חזרות…", 16);
        _input = input;
        var send = Kit.IconButton(Icons.Send, 38, () => Ask(_input.Text), PillKind.Ghost, 18, Tone.OnPrimary);
        send.Background = Tone.Primary;
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.Children.Add(inputHost);
        Grid.SetColumn(send, 1);
        field.Children.Add(send);
        var frame = new Border
        {
            Height = 54, CornerRadius = new CornerRadius(27), Background = Tone.B("#12FFFFFF"), BorderBrush = Tone.B("#1AFFFFFF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(20, 0, 8, 0), Child = field, Margin = new Thickness(0, 18, 0, 0),
        };
        _input.GotKeyboardFocus += (_, _) => frame.BorderBrush = Tone.AccentLine;
        _input.LostKeyboardFocus += (_, _) => frame.BorderBrush = Tone.B("#1AFFFFFF");
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Ask(_input.Text);
        };
        _input.TextChanged += (_, _) =>
        {
            if (_busy.Visibility != Visibility.Visible) _avatar.Mood = PalonMood.Listen;
        };
        root.Children.Add(frame);

        _suggestions.Margin = new Thickness(0, 18, 0, 0);
        _suggestions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _suggestions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _suggestions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chips = new (string Q, Func<(string[] Steps, string Answer)> Local)[]
        {
            ("כמה הפקדות ביום צריך כדי להגיע ליעד?", PaceAnswer),
            ("כמה אני אמור לקבל החודש?", PayAnswer),
            ("סכם לי את השבוע בשביל המנהל", WeekAnswer),
            ("למי עוד לא חזרתי היום?", LateAnswer),
        };
        for (var i = 0; i < chips.Length; i++)
        {
            var (q, local) = chips[i];
            var chip = new Border
            {
                CornerRadius = new CornerRadius(20), BorderBrush = Tone.B("#14FFFFFF"), BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 14, 16, 14), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
                Margin = new Thickness(0, i >= 2 ? 10 : 0, 0, 0),
                Child = Kit.T(q, 14, Tone.TextSoft, wrap: true),
            };
            Kit.HoverFill(chip, Tone.B("#0DFFFFFF"), Tone.Track);
            Kit.Press(chip);
            Kit.Clickable(chip, () => AskLocal(q, local));
            if (_suggestions.RowDefinitions.Count <= i / 2) _suggestions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(chip, i / 2);
            Grid.SetColumn(chip, i % 2 * 2);
            _suggestions.Children.Add(chip);
        }
        root.Children.Add(_suggestions);

        _question = Kit.T("", 15, Tone.Text, FontWeights.SemiBold, wrap: true);
        _busy.Margin = new Thickness(0, 18, 0, 0);
        _busy.Children.Add(_question);
        _busy.Children.Add(_steps);

        _answer = new TextBlock { FontSize = 15.5, FontFamily = Font.Family, Foreground = Tone.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 27 };
        _caret = new Border { Width = 2, Height = 17, Background = Tone.Text, Margin = new Thickness(2, 0, 0, -3) };
        var answerCol = new StackPanel();
        answerCol.Children.Add(_answer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Kit.Pill("העתק תשובה", PillKind.Secondary, () => _host.CopyWithToast(_full, "התשובה הועתקה"), height: 34, fontSize: 13));
        var again = Kit.Pill("שאלה אחרת", PillKind.Ghost, Reset, height: 34, fontSize: 13);
        ((TextBlock)again.Child).Foreground = Tone.MutedSoft;
        again.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(again);
        answerCol.Children.Add(actions);
        _answerCard = new Border
        {
            CornerRadius = new CornerRadius(22), Background = Tone.FillSoft, BorderBrush = Tone.B("#14FFFFFF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 14, 0, 0),
            Child = answerCol, Visibility = Visibility.Collapsed,
        };
        _busy.Children.Add(_answerCard);
        root.Children.Add(_busy);
        Child = root;

        _type.Tick += (_, _) => TypeTick();
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _avatar.Mood = PalonMood.Idle;
        };
    }

    public void FocusInput() => Dispatcher.BeginInvoke(() => _input.Focus(), DispatcherPriority.Input);

    public void Cancel()
    {
        _cts?.Cancel();
        _type.Stop();
        _settle.Stop();
    }

    void Reset()
    {
        Cancel();
        _generation++;
        _busy.Visibility = Visibility.Collapsed;
        _suggestions.Visibility = Visibility.Visible;
        _input.Text = "";
        _status.Text = "אני מקשיב. מה תרצה לדעת?";
        _avatar.Mood = PalonMood.Listen;
        Kit.Rise(_suggestions.Children.OfType<FrameworkElement>());
        FocusInput();
    }

    void Begin(string question)
    {
        Cancel();
        _generation++;
        _suggestions.Visibility = Visibility.Collapsed;
        _busy.Visibility = Visibility.Visible;
        _answerCard.Visibility = Visibility.Collapsed;
        _question.Text = question;
        Kit.Auto(_question);
        _steps.Children.Clear();
        _status.Text = "רגע, בודק…";
        _avatar.Mood = PalonMood.Think;
    }

    /// <summary>A step line: a spinner-free dot that pops to a green check when done.</summary>
    (Border Dot, TextBlock Label) AddStep(string label)
    {
        var dot = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderBrush = Tone.AccentLine, BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = Kit.T(label, 13.5, Tone.Faint);
        text.Margin = new Thickness(10, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, _steps.Children.Count > 0 ? 8 : 0, 0, 0) };
        row.Children.Add(dot);
        row.Children.Add(text);
        _steps.Children.Add(row);
        Kit.Rise(new[] { row });
        return (dot, text);
    }

    static void CompleteStep((Border Dot, TextBlock Label) step)
    {
        step.Dot.BorderThickness = new Thickness(0);
        step.Dot.Background = Tone.Green;
        var tick = Kit.Icon(Icons.Check, 11, Tone.OnPrimary, 3.2);
        step.Dot.Child = tick;
        Kit.Pop(tick);
        step.Label.Foreground = Tone.MutedSoft;
    }

    // ---- local answers -------------------------------------------------------------

    static (MonthBook? Book, MonthStats? Stats, BonusRules Rules) Month()
    {
        var now = DateTime.Now;
        var rules = SalesStore.LoadRules();
        var book = SalesStore.Get(now.Year, now.Month);
        return (book, book is null ? null : SalesStats.Compute(book, rules, now), rules);
    }

    static (string[], string) PaceAnswer()
    {
        var (book, s, _) = Month();
        if (book is null || s is null) return (new[] { "קורא את החודש" }, "עוד לא פתחת את החודש. פתח אותו עם היעד, ואחשב לך את הקצב.");
        return (new[] { $"קורא את {He.Month(book.Month)} · {s.Count} עסקאות", $"סופר ימי עבודה · {s.RemainingWorkDays} נותרו", "מחשב קצב ותחזית" }, MonthView.AnswerPace(s));
    }

    static (string[], string) PayAnswer()
    {
        var (book, s, rules) = Month();
        if (book is null || s is null) return (new[] { "קורא את החודש" }, $"עוד אין עסקאות החודש: בסיס ₪{He.N(rules.BaseSalaryIls)}.");
        return (new[] { "מחשב בונוסי FTD לפי הכללים שלך", "בודק בונוס יעד", "משווה למה שאושר" }, MonthView.AnswerPay(s, rules));
    }

    static (string[], string) WeekAnswer()
    {
        var (book, s, _) = Month();
        if (book is null || s is null) return (new[] { "אוסף את עסקאות השבוע" }, "אין עסקאות רשומות החודש.");
        return (new[] { "אוסף את עסקאות השבוע", "מסכם לפי מקור ודרגה", "מנסח סיכום" }, MonthView.AnswerWeek(book, s, DateTime.Now));
    }

    static (string[], string) LateAnswer() =>
        (new[] { "בודק חזרות פתוחות", "ממיין לפי דחיפות" }, MonthView.AnswerLate(CallbackStore.Load(), DateTime.Now));

    void AskLocal(string question, Func<(string[] Steps, string Answer)> compute)
    {
        Begin(question);
        var gen = _generation;
        (string[] steps, string answer) result;
        try
        {
            result = compute();
        }
        catch (Exception ex)
        {
            Log.Write($"Ask local answer failed: {ex.Message}");
            result = (Array.Empty<string>(), "משהו השתבש בקריאת הנתונים — ראה לוג.");
        }
        // The computation is instant; the steps land one beat apart so each is readable.
        var i = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        (Border, TextBlock)? previous = null;
        timer.Tick += (_, _) =>
        {
            if (gen != _generation) { timer.Stop(); return; }
            if (previous is { } p) CompleteStep(p);
            if (i < result.steps.Length)
            {
                previous = AddStep(result.steps[i++]);
                return;
            }
            timer.Stop();
            ShowAnswer(result.answer);
        };
        timer.Start();
    }

    // ---- the agent -------------------------------------------------------------------

    static readonly Dictionary<string, string> ToolSteps = new()
    {
        ["open_command"] = "פותח את הפקודה",
        ["open_url"] = "פותח קישור",
        ["create_reminder"] = "קובע חזרה",
        ["list_reminders"] = "בודק חזרות",
        ["search_notes"] = "מחפש בהערות מהשיחות",
        ["get_call_stats"] = "סופר שיחות",
        ["open_whatsapp"] = "פותח וואטסאפ",
        ["daily_recap"] = "מסכם את היום",
        ["control_music"] = "שולט במוזיקה",
    };

    /// <summary>Wraps a tool so each call shows up as a step while it runs.</summary>
    sealed class ObservedTool : AgentTool
    {
        readonly AgentTool _inner;
        readonly Action<string> _started;

        public ObservedTool(AgentTool inner, Action<string> started)
        {
            _inner = inner;
            _started = started;
        }

        public override string Name => _inner.Name;
        public override string Description => _inner.Description;
        public override string ParametersJson => _inner.ParametersJson;

        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            _started(Name);
            return _inner.ExecuteAsync(args, ct);
        }
    }

    async void Ask(string text)
    {
        var question = text.Trim();
        if (question.Length == 0) return;
        _input.Text = "";
        Begin(question);
        var gen = _generation;
        if (!AiChat.HasKey)
        {
            ShowAnswer("כדי לשאול שאלות חופשיות צריך מפתח Gemini או DeepSeek (בהגדרות, תחת Notes & dictation). השאלות המוצעות עובדות גם בלי.");
            return;
        }
        var thinking = AddStep("חושב");
        var open = new List<(Border, TextBlock)> { thinking };
        void Started(string tool) => Dispatcher.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            foreach (var s in open) CompleteStep(s);
            open.Clear();
            open.Add(AddStep(ToolSteps.TryGetValue(tool, out var label) ? label : tool));
        });
        var tools = ToolRegistry.CreateDefault().Select(t => (AgentTool)new ObservedTool(t, Started)).ToList();
        _session ??= new AssistantSession(_host.CallStateSource, _host.CurrentNumberSource);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string answer;
        try
        {
            // On the UI context, like the voice path: some tools touch windows.
            var result = await AgentLoop.RunAsync(_session, question, ct, tools);
            if (result.Failure == AgentFailure.Cancelled || ct.IsCancellationRequested) return; // closed mid-answer — stay quiet
            answer = result.Outcome?.Text ?? (result.Failure == AgentFailure.ProviderDown
                ? "Palon לא מצליח להגיע למודל כרגע — נסה שוב עוד רגע."
                : "לא הצלחתי למצוא תשובה. נסה לנסח אחרת.");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal Ask failed: {ex}");
            answer = "משהו השתבש — ראה לוג.";
        }
        if (gen != _generation) return;
        foreach (var s in open) CompleteStep(s);
        ShowAnswer(answer);
    }

    void ShowAnswer(string answer)
    {
        _full = answer;
        _shown = 0;
        _status.Text = "הנה מה שמצאתי";
        _answer.Inlines.Clear();
        _answer.FlowDirection = Bidi.HasRtl(answer) || answer.Length == 0 ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _answerCard.Visibility = Visibility.Visible;
        Kit.Rise(new[] { _answerCard });
        _avatar.Mood = PalonMood.Talk;
        var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
        _caret.BeginAnimation(OpacityProperty, blink);
        _type.Start();
    }

    void TypeTick()
    {
        _shown = Math.Min(_full.Length, _shown + 3);
        _answer.Inlines.Clear();
        _answer.Inlines.Add(new Run(_full[.._shown]));
        if (_shown < _full.Length)
        {
            _answer.Inlines.Add(new InlineUIContainer(_caret) { BaselineAlignment = BaselineAlignment.Baseline });
            return;
        }
        _type.Stop();
        _caret.BeginAnimation(OpacityProperty, null);
        _avatar.Mood = PalonMood.Happy;
        _avatar.Cheer();
        _settle.Interval = TimeSpan.FromMilliseconds(1600);
        _settle.Start();
    }
}
