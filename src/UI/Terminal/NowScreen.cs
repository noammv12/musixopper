using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Palon.Agentic;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Now: the Terminal's one screen. Palon on the right with his line and
/// nudges; the action stack in the middle (only callbacks the rep set that
/// are due, and work Palon already prepared) over the quick-access shelf;
/// pay and the daily rings on the left; the command bar along the bottom.
/// Everything else lives in side sheets opened from the rail.
/// </summary>
sealed partial class NowScreen : TerminalScreen
{
    readonly NowState _state;
    readonly Canvas _fx = new() { IsHitTestVisible = false, ClipToBounds = false };

    // hero
    readonly PalonAvatar _hero = new() { Width = 180, Height = 180, Mood = PalonMood.Idle };
    readonly Border _heroGlow;
    readonly TextBlock _speech;
    readonly StackPanel _nudges = new();
    readonly ContentControl _result = new() { Focusable = false };
    readonly StackPanel _heroExtras = new();
    readonly DispatcherTimer _typer = new() { Interval = TimeSpan.FromMilliseconds(28) };
    readonly DispatcherTimer _moodSettle = new();
    string _speechFull = "";
    int _speechShown;
    DateTime _speechHoldUntil;
    PalonMood? _barMood;

    // pay
    readonly Border _payCard;
    readonly Odometer _pay = new(46, Tone.Text);
    readonly TextBlock _payBasis = Kit.T("", 12.5, Tone.Muted);
    readonly ScaleTransform _payFill = new(0, 1);
    readonly TextBlock _payDeals = Kit.T("", 13, Tone.Text, FontWeights.Medium);
    readonly TextBlock _payLeft = Kit.T("", 12.5, Tone.Muted);
    readonly Border _streak;
    readonly TextBlock _streakText = Kit.T("", 12, Tone.Text, FontWeights.Medium);
    readonly Border _gong;
    int _lastPay = -1, _lastCount = -1;

    // rings
    readonly TripleRing _rings = new(150);
    readonly StackPanel _ringRows = new();
    readonly TextBlock _ringsHead = Kit.T("", 12.5, Tone.Muted);
    bool _ringsShown;

    // stack + shelf + bar
    readonly Grid _stackHost = new() { Height = 344 };
    readonly TextBlock _stackMore = Kit.T("", 12.5, Tone.Muted);
    readonly Border _shelfHost = new();
    readonly CommandBar _bar;

    bool _shiny;
    bool _ritualsChecked;
    TermData? _data;
    List<NowCard> _cards = new();
    IReadOnlyList<DayRing> _dayRings = Array.Empty<DayRing>();

    public NowScreen(TerminalWindow host) : base(host)
    {
        _state = NowState.Load();
        _state.Opens++;
        _shiny = NowRituals.IsShiny(Random.Shared.Next(NowRituals.ShinyOdds));
        _state.Save();

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260, MaxWidth = 380 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star), MinWidth = 400, MaxWidth = 560 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 270, MaxWidth = 350 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); // the rail's lane (visual left)
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Margin = new Thickness(36, 22, 0, 18);

        // ---- hero (visual right) ----
        _heroGlow = new Border
        {
            Width = 340, Height = 300, IsHitTestVisible = false, Margin = new Thickness(0, -20, 0, 0),
            Background = Fx.Glow("#1AC8D2E6", "#00000000"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        _speech = new TextBlock
        {
            FontFamily = Font.Family, FontSize = 20, FontWeight = FontWeights.Light, Foreground = Tone.Text,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 29, MinHeight = 84,
            Margin = new Thickness(0, 8, 0, 0), MaxWidth = 340,
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_speech, System.Windows.Automation.AutomationLiveSetting.Polite);
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _hero.HorizontalAlignment = HorizontalAlignment.Center;
        _hero.Margin = new Thickness(0, 18, 0, 0);
        hero.Children.Add(_hero);
        hero.Children.Add(_speech);
        _heroExtras.Margin = new Thickness(0, 14, 0, 0);
        hero.Children.Add(_heroExtras);
        _result.Margin = new Thickness(0, 10, 0, 0);
        hero.Children.Add(_result);
        _nudges.Margin = new Thickness(0, 10, 0, 0);
        hero.Children.Add(_nudges);
        var heroCell = new Grid();
        heroCell.Children.Add(_heroGlow);
        heroCell.Children.Add(hero);
        Grid.SetColumn(heroCell, 0);
        Children.Add(heroCell);
        Kit.Clickable(_hero, () => Cheer());
        _hero.Cursor = Cursors.Hand;
        _hero.ToolTip = "Palon";

        // ---- center: stack + shelf ----
        var center = new DockPanel { LastChildFill = true };
        var stackHead = Kit.Bar(Fx.Kicker("הבא בתור", Tone.MutedSoft), _stackMore);
        stackHead.Margin = new Thickness(6, 4, 6, 12);
        DockPanel.SetDock(stackHead, Dock.Top);
        center.Children.Add(stackHead);
        DockPanel.SetDock(_stackHost, Dock.Top);
        center.Children.Add(_stackHost);
        _shelfHost.Margin = new Thickness(0, 22, 0, 0);
        _shelfHost.VerticalAlignment = VerticalAlignment.Top;
        center.Children.Add(_shelfHost);
        Grid.SetColumn(center, 2);
        Children.Add(center);

        // ---- visual left: pay + rings ----
        _gong = new Border
        {
            Width = 560, Height = 560, IsHitTestVisible = false, Opacity = 0, Margin = new Thickness(-110, -210, -110, -210),
            Background = Fx.Glow("#6BECBE69", "#00000000"), RenderTransform = new ScaleTransform(0.2, 0.2), RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _streak = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3, 9, 3), Background = Tone.FillSoft,
            BorderBrush = Tone.B("#1FFFFFFF"), BorderThickness = new Thickness(1), Child = _streakText, Visibility = Visibility.Collapsed,
        };
        _payCard = BuildPay();
        var ringsCard = BuildRings();
        var left = new StackPanel();
        var payCell = new Grid();
        payCell.Children.Add(_gong);
        payCell.Children.Add(_payCard);
        left.Children.Add(payCell);
        ringsCard.Margin = new Thickness(0, 16, 0, 0);
        left.Children.Add(ringsCard);
        Grid.SetColumn(left, 4);
        Children.Add(left);

        // ---- command bar ----
        _bar = new CommandBar(host, this) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Width = 680 };
        Grid.SetRow(_bar, 1);
        Grid.SetColumnSpan(_bar, 5);
        Children.Add(_bar);

        Grid.SetRowSpan(_fx, 2);
        Grid.SetColumnSpan(_fx, 6);
        Panel.SetZIndex(_fx, 10);
        Children.Add(_fx);

        _typer.Tick += (_, _) => TypeSpeech();
        _moodSettle.Tick += (_, _) =>
        {
            _moodSettle.Stop();
            ApplyMood();
        };

        NudgeHub.Raised += OnNudge;
        NudgeHub.Dismissed += OnNudgeDismissed;
        Loaded += (_, _) => { foreach (var n in NudgeHub.Active.TakeLast(2)) AddNudge(n, animate: false); };
        Unloaded += (_, _) =>
        {
            _typer.Stop();
            _moodSettle.Stop();
        };
        host.Closed += (_, _) =>
        {
            NudgeHub.Raised -= OnNudge;
            NudgeHub.Dismissed -= OnNudgeDismissed;
            _state.LastActiveLocal = DateTime.Now;
            _state.Save();
        };
        host.Deactivated += (_, _) => _state.LastActiveLocal = DateTime.Now;
        host.Activated += (_, _) => OnActivated();
    }

    public override string Title => "עכשיו";

    public CommandBar Bar => _bar;

    // ---- render ---------------------------------------------------------------------

    public override void Render(TermData d, bool entrance)
    {
        _data = d;
        var templates = TemplatesStore.Load();
        var calls = CallStatsStore.Load();
        var handled = _state.Handled.Keys.ToHashSet();
        _cards = NowStack.Build(d.Callbacks, d.Notes, templates, handled, d.Now);
        _cards = _cards.Concat(PlanCards(d, templates, handled)).Take(NowStack.MaxCards).ToList();
        _dayRings = DayRings.Build(calls, d.Callbacks, d.Book, d.Stats, d.Now, Settings.CallsGoal);
        var animate = Fx.Allowed(Host);

        RenderPay(d, entrance && animate);
        RenderRings(entrance && animate);
        RenderStack((entrance || _riseNext) && animate);
        _riseNext = false;
        RenderShelf(d, templates);
        if (DateTime.Now >= _speechHoldUntil) Say(HeroLine(), quiet: !entrance);
        RenderHeroExtras(d, calls);

        if (entrance && !_ritualsChecked)
        {
            _ritualsChecked = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OpeningRituals(d));
        }
    }

    /// <summary>The brain's plan items that belong on the stack (see NowStack.FromPlan).</summary>
    IEnumerable<NowCard> PlanCards(TermData d, IReadOnlyList<Terminal.MessageTemplate> templates, ISet<string> handled)
    {
        try
        {
            var snapshot = AgenticRouter.Snapshot();
            var plan = NextSteps.Plan(snapshot);
            var notes = d.Notes.ToDictionary(n => n.Id);
            return NowStack.FromPlan(plan, templates, handled, _cards,
                item => item.NoteId is { } id && notes.TryGetValue(id, out var n)
                    ? BuyingSignals.Detect(n).Select(BuyingSignals.TemplateFor).FirstOrDefault(t => t is not null)
                    : null,
                item => item.NoteId is { } id && snapshot.NamedClients.FirstOrDefault(c => c.Name == item.Who) is { } card
                    ? FollowUpDrafts.Cached(card.Key, id)?.Text
                    : null);
        }
        catch (Exception ex)
        {
            Log.Write($"Now plan cards failed: {ex.Message}");
            return Array.Empty<NowCard>();
        }
    }

    string HeroLine()
    {
        var d = _data!;
        return NowLines.Hero(d.Now, Settings.RepName, _cards.FirstOrDefault(), _dayRings,
            Host.CallStateSource() == CallState.OnCall, d.Counts, d.Stats);
    }

    /// <summary>The person the rep is working with right now — the top card's —
    /// whose first name templates fill in.</summary>
    public string? ActiveFirstName
    {
        get
        {
            var top = _cards.FirstOrDefault();
            if (top is null || top.Who == "שיחה" || top.Who.Any(char.IsDigit)) return null;
            var first = TemplateFill.FirstName(top.Who);
            return first.Length > 0 ? first : null;
        }
    }

    // ---- hero: speech, mood, cheer ------------------------------------------------------

    /// <summary>Palon says a line under his portrait (typewriter while he talks).
    /// <paramref name="quiet"/> lands it without the talk animation.</summary>
    public void Say(string text, bool quiet = false, int holdMs = 0)
    {
        if (holdMs > 0) _speechHoldUntil = DateTime.Now.AddMilliseconds(holdMs);
        if (text == _speechFull) return;
        _speechFull = text;
        if (quiet || !Fx.Allowed(Host))
        {
            _typer.Stop();
            _speechShown = text.Length;
            _speech.Text = text;
            ApplyMood();
            return;
        }
        _speechShown = 0;
        _speech.Text = "";
        _typer.Start();
        ApplyMood();
    }

    void TypeSpeech()
    {
        _speechShown = Math.Min(_speechFull.Length, _speechShown + 1);
        _speech.Text = _speechFull[.._speechShown] + (_speechShown < _speechFull.Length ? "▍" : "");
        if (_speechShown < _speechFull.Length) return;
        _typer.Stop();
        ApplyMood();
    }

    /// <summary>The command bar sets Palon's mood while it listens/thinks/talks; null hands it back.</summary>
    public void BarMood(PalonMood? mood)
    {
        _barMood = mood;
        ApplyMood();
    }

    void ApplyMood()
    {
        if (_moodSettle.IsEnabled) return; // a cheer is playing
        _hero.Mood = Host.CallStateSource() == CallState.OnCall ? PalonMood.Listen
            : _barMood ?? (_typer.IsEnabled ? PalonMood.Talk : PalonMood.Idle);
    }

    /// <summary>Palon's hop — happy for a beat, then back.</summary>
    public void Cheer(bool soft = false)
    {
        if (!Fx.Allowed(Host)) return;
        _hero.Mood = PalonMood.Happy;
        if (!soft) _hero.Cheer();
        _moodSettle.Stop();
        _moodSettle.Interval = TimeSpan.FromMilliseconds(soft ? 1200 : 1800);
        _moodSettle.Start();
    }

    // ---- result card (AgenticHost.ShowText) ----------------------------------------------

    /// <summary>A longer answer from the brain (a draft, a brief, a search, a client summary):
    /// a glass card under Palon with Copy and close. One at a time — a new one replaces it.</summary>
    public void ShowResult(string title, string body)
    {
        var card = Fx.Glass2(22, new Thickness(16, 14, 16, 14));
        var col = new StackPanel();
        var head = Kit.T(title, 12.5, Tone.TextSoft, FontWeights.SemiBold);
        head.VerticalAlignment = VerticalAlignment.Center;
        var close = Kit.IconButton(Icons.Close, 24, () => CloseResult(card), icon: 11);
        System.Windows.Automation.AutomationProperties.SetName(close, "סגור");
        col.Children.Add(Kit.Bar(head, close));
        var text = new TextBox
        {
            Text = body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Tone.Text, FontFamily = Font.Family, FontSize = 14,
            Template = Kit.BareTextBoxTemplate(multiline: true), FocusVisualStyle = null,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260, Margin = new Thickness(0, 6, 0, 0),
            // Mixed Hebrew/English text: let each paragraph pick its own direction.
            FlowDirection = Bidi.HasRtl(body) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        col.Children.Add(text);
        var copy = Kit.Pill("העתק", PillKind.Secondary, () => Host.CopyWithToast(body, "הועתק"), height: 32, fontSize: 13);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Margin = new Thickness(0, 10, 0, 0);
        col.Children.Add(copy);
        card.Child = col;
        _result.Content = card;
        Say(title, quiet: true, holdMs: 6000);
        if (Fx.Allowed(Host)) Kit.Rise(new[] { card });
    }

    void CloseResult(Border card)
    {
        if (!ReferenceEquals(_result.Content, card)) return;
        if (!Fx.Allowed(Host))
        {
            _result.Content = null;
            return;
        }
        var fade = Feel.To(0, 200, Motion.Out);
        fade.Completed += (_, _) => { if (ReferenceEquals(_result.Content, card)) _result.Content = null; };
        card.BeginAnimation(OpacityProperty, fade);
    }

    // ---- nudges (NudgeHub) -------------------------------------------------------------

    void OnNudge(Nudge nudge) => Dispatcher.BeginInvoke(() => AddNudge(nudge, animate: true));

    void OnNudgeDismissed(string id) => Dispatcher.BeginInvoke(() => RemoveNudge(id));

    void AddNudge(Nudge nudge, bool animate)
    {
        RemoveNudge(nudge.Id, animate: false);
        var card = Fx.Glass2(22, new Thickness(16, 14, 16, 14));
        card.Tag = nudge.Id;
        card.Margin = new Thickness(0, 0, 0, 10);
        var col = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Kit.T("Palon", 12.5, Tone.TextSoft, FontWeights.SemiBold));
        var time = Kit.Mono(He.Clock(nudge.AtLocal), 11.5, Tone.Faint);
        time.Margin = new Thickness(8, 0, 0, 0);
        time.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(time);
        var close = Kit.IconButton(Icons.Close, 24, () => NudgeHub.Dismiss(nudge.Id), icon: 11);
        System.Windows.Automation.AutomationProperties.SetName(close, "סגור");
        col.Children.Add(Kit.Bar(head, close));
        var text = Kit.T(nudge.Text, 14, Tone.Text, wrap: true);
        text.LineHeight = 21;
        text.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(text);
        if (nudge.ActionLabel is { } label && nudge.Act is { } act)
        {
            var go = Kit.Pill(label, PillKind.Secondary, async () =>
            {
                try
                {
                    await act();
                }
                catch (Exception ex)
                {
                    Log.Write($"Nudge action failed: {ex}");
                    Host.Toast("הפעולה לא הצליחה — ראה לוג");
                }
                NudgeHub.Dismiss(nudge.Id);
            }, height: 32, fontSize: 13);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 10, 0, 0);
            col.Children.Add(go);
        }
        card.Child = col;
        _nudges.Children.Insert(0, card);
        while (_nudges.Children.Count > 2) _nudges.Children.RemoveAt(_nudges.Children.Count - 1); // max 2 near him
        if (animate && Fx.Allowed(Host))
        {
            Kit.Rise(new[] { card });
            if (nudge.Kind == NudgeKind.Celebration) Cheer();
        }
    }

    void RemoveNudge(string id, bool animate = true)
    {
        var card = _nudges.Children.OfType<Border>().FirstOrDefault(b => b.Tag as string == id);
        if (card is null) return;
        if (!animate || !Fx.Allowed(Host))
        {
            _nudges.Children.Remove(card);
            return;
        }
        var fade = Feel.To(0, 200, Motion.Out);
        fade.Completed += (_, _) => _nudges.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    // ---- pay -----------------------------------------------------------------------------

    Border BuildPay()
    {
        var card = Kit.Glass(28, new Thickness(24, 22, 24, 20), lift: true);
        card.Cursor = Cursors.Hand;
        var col = new StackPanel();
        col.Children.Add(Kit.Bar(Fx.Kicker("השכר שלך החודש", Tone.MutedSoft), _streak));
        var amount = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var shekel = Kit.Num("₪", 30, Tone.MutedSoft, FontWeights.Light);
        shekel.VerticalAlignment = VerticalAlignment.Center;
        shekel.Margin = new Thickness(0, 0, 4, 0);
        amount.Children.Add(shekel);
        amount.Children.Add(_pay);
        col.Children.Add(amount);
        _payBasis.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(_payBasis);
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Tone.Track, Margin = new Thickness(0, 16, 0, 0) };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Tone.C("#FFFFFFFF"), Tone.C("#FF9AA0AA"), 0),
            RenderTransform = _payFill, RenderTransformOrigin = new Point(0, 0.5),
        };
        track.Child = fill;
        col.Children.Add(track);
        var foot = Kit.Bar(_payDeals, _payLeft);
        foot.Margin = new Thickness(0, 10, 0, 0);
        col.Children.Add(foot);
        card.Child = col;
        Kit.Press(card, 0.98);
        Kit.Clickable(card, () => Host.Navigate(TerminalPage.Month));
        System.Windows.Automation.AutomationProperties.SetName(card, "השכר שלך החודש — פתח את החודש");
        return card;
    }

    void RenderPay(TermData d, bool animate)
    {
        var s = d.Stats;
        var pay = s?.ExpectedPayIls ?? d.Rules.BaseSalaryIls;
        var count = s?.Count ?? 0;
        var delta = _lastPay >= 0 ? PayMath.DepositDelta(_lastCount, _lastPay, count, pay) : null;
        var roll = animate || (_lastPay >= 0 && pay != _lastPay && Fx.Allowed(Host));
        if (animate && _lastPay < 0) _pay.Set(d.Rules.BaseSalaryIls, false);
        _pay.Set(pay, roll);
        _lastPay = pay;
        _lastCount = count;

        _payBasis.Text = $"בסיס {He.N(d.Rules.BaseSalaryIls)} + FTD ₪{He.N(s?.FtdIls ?? 0)}";
        if (s?.Target is int t && t > 0)
        {
            SetFill(_payFill, (double)count / t, roll);
            _payDeals.Text = $"{count} / {t} עסקאות";
            _payLeft.Text = s.Remaining is 0 ? "בבונוס יעד" : s.RequiredPerDay is double need ? $"חסרים {s.Remaining} · {He.R1(need)} ביום" : $"חסרים {s.Remaining}";
        }
        else
        {
            SetFill(_payFill, 0, false);
            _payDeals.Text = count == 1 ? "עסקה אחת החודש" : $"{count} עסקאות החודש";
            _payLeft.Text = "אין יעד עדיין";
        }
        var streak = PayMath.Streak(d.Book, d.Now);
        _streak.Visibility = streak >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _streakText.Text = $"{streak} ימים ברצף";
        if (delta is int plus) Deposit(plus);
    }

    static void SetFill(ScaleTransform fill, double fraction, bool animate)
    {
        var to = Math.Clamp(fraction, 0, 1);
        if (animate) fill.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.To(to, 1000));
        else
        {
            fill.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            fill.ScaleX = to;
        }
    }

    /// <summary>A deposit landed: the gong glow, "+₪" flying into the counter, Palon's hop.</summary>
    void Deposit(int plus)
    {
        var holiday = NowRituals.Holiday(DateTime.Now);
        if (holiday is not null && !_holidayDepositSaid)
        {
            _holidayDepositSaid = true;
            Say(holiday.FirstDepositLine, holdMs: 6000);
        }
        else Say($"+₪{He.N(plus)}. {HeroLine()}", holdMs: 4000);
        if (!Fx.Allowed(Host)) return;
        Cheer();
        var scale = (ScaleTransform)_gong.RenderTransform;
        var grow = Feel.FromTo(0.2, 1.1, 1600);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1800) };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3), Feel.Expo));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), Motion.Out));
        _gong.BeginAnimation(OpacityProperty, glow);
        Fly($"+₪{He.N(plus)}");
    }

    bool _holidayDepositSaid;

    void Fly(string text)
    {
        if (ActualWidth <= 0) return;
        var t = Kit.Num(text, 34, Tone.Text, FontWeights.Medium);
        t.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.5 };
        var from = _stackHost.TranslatePoint(new Point(_stackHost.ActualWidth / 2, 60), _fx);
        var to = _pay.TranslatePoint(new Point(_pay.ActualWidth / 2, 10), _fx);
        Canvas.SetLeft(t, 0);
        Canvas.SetTop(t, 0);
        var move = new TranslateTransform(from.X, from.Y);
        var scale = new ScaleTransform(0.6, 0.6);
        t.RenderTransform = new TransformGroup { Children = { scale, move } };
        _fx.Children.Add(t);
        move.BeginAnimation(TranslateTransform.XProperty, Feel.FromTo(from.X, to.X - 40, 1100));
        move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(from.Y, to.Y - 20, 1100, Motion.InOut));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(0.6, 1.1, 700));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.FromTo(0.6, 1.1, 700));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1300) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.15)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.75)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Completed += (_, _) => _fx.Children.Remove(t);
        t.BeginAnimation(OpacityProperty, fade);
    }

    // ---- rings -----------------------------------------------------------------------------

    Border BuildRings()
    {
        var card = Kit.Glass(28, new Thickness(20, 18, 20, 16));
        var col = new StackPanel();
        col.Children.Add(Kit.Bar(Fx.Kicker("היעדים של היום", Tone.MutedSoft), _ringsHead));
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(_ringRows);
        _rings.VerticalAlignment = VerticalAlignment.Center;
        _rings.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_rings, 1);
        g.Children.Add(_rings);
        col.Children.Add(g);
        card.Child = col;
        _rings.Width = _rings.Height = 128;
        return card;
    }

    void RenderRings(bool animate)
    {
        _rings.Set(_dayRings, animate || !_ringsShown && Fx.Allowed(Host));
        _ringsShown = true;
        _ringsHead.Text = DayRings.Head(_dayRings);
        _ringRows.Children.Clear();
        for (var i = 0; i < _dayRings.Count; i++)
        {
            var r = _dayRings[i];
            var col = new StackPanel();
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(Kit.Dot(TripleRing.Dots[i], 7));
            var label = Kit.T(r.Name, 12.5, Tone.MutedSoft);
            label.Margin = new Thickness(6, 0, 0, 0);
            name.Children.Add(label);
            col.Children.Add(name);
            var value = new TextBlock { FontFamily = Font.Family, FontSize = 19, Foreground = Tone.Text, FontWeight = FontWeights.Medium };
            value.Inlines.Add(new System.Windows.Documents.Run(He.R1(r.Value)));
            value.Inlines.Add(new System.Windows.Documents.Run($" / {He.R1(r.Goal)}") { Foreground = Tone.Muted, FontSize = 13, FontWeight = FontWeights.Normal });
            col.Children.Add(value);
            col.Children.Add(Kit.T(r.Hint, 11.5, r.Closed ? Tone.B("#FFA6D6BE") : Tone.Muted));
            var row = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 6, 10, 6), Child = col, Cursor = Cursors.Hand };
            Kit.HoverFill(row, Tone.B("#00FFFFFF"), Tone.RowHover);
            Kit.Press(row, 0.97);
            var key = r.Key;
            var ring = r;
            Kit.Clickable(row, () => RingAction(key, ring));
            _ringRows.Children.Add(row);
        }
    }

    void RingAction(string key, DayRing ring)
    {
        switch (key)
        {
            case "cbs":
                Host.Navigate(TerminalPage.Callbacks);
                break;
            case "deps":
                Host.Navigate(TerminalPage.Month);
                break;
            default:
                Say(ring.Closed ? "טבעת השיחות סגורה. כל שיחה עכשיו היא בונוס." : $"עוד {Math.Max(0, ring.Goal - ring.Value)} שיחות לסגור את הטבעת.", holdMs: 5000);
                break;
        }
    }
}
