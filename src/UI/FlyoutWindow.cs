using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bridget.Interop;
using WinF = System.Windows.Forms;

namespace Bridget.UI;

/// <summary>
/// The single window of the app: status, trigger mode, toggles — plus the
/// two first-run onboarding panels. Opens near the tray icon, dismisses on
/// focus loss.
/// </summary>
sealed partial class FlyoutWindow : Window
{
    const double ShadowMargin = 20;  // room around the card for the drop shadow
    const double EdgeGap = 8;        // visual gap between the card and the taskbar

    readonly CallEngine _engine;
    readonly Border _root;
    readonly Grid _panelHost; // the panels grid — _root.Child is the scroll host, not this
    readonly TranslateTransform _rootSlide = new();
    readonly ScaleTransform _rootScale = new(1, 1);

    // main panel
    readonly StackPanel _mainPanel;
    Ellipse _statusDot = null!;
    Ellipse _statusHalo = null!;
    ScaleTransform _haloScale = null!;
    TextBlock _statusText = null!;
    Segmented _segmented = null!;
    TextBlock _caption = null!;
    TextBlock _setupLink = null!;
    PillSwitch _switchEnabled = null!;
    PillSwitch _switchStartup = null!;
    TextBlock _hotkeyCaption = null!;

    // onboarding
    readonly StackPanel _welcomePanel;
    readonly StackPanel _softphonePanel;
    TextBlock _softphoneNotice = null!;
    Border _cardMic = null!;
    Border _cardEvents = null!;
    int _welcomeSelection;

    // snippets
    readonly StackPanel _snippetsPanel;
    StackPanel _snippetList = null!;
    TextBlock _addSnippetLink = null!;
    List<Snippet> _snippets = new();
    int _editingIndex = -1;

    readonly DispatcherTimer _statusOverrideTimer;
    string? _statusOverride;
    DateTime _lastHiddenAt = DateTime.MinValue;
    bool _hiding;
    bool _pulsing;

    // positioning + drag
    System.Drawing.Point _anchor;   // cursor at open time; repositions stay anchored to it
    bool _userMoved;
    bool _morphing;                 // height-morph animation owns Height/Top right now
    bool _dragArmed;
    bool _dragging;
    NativeMethods.POINT _dragStartPt;
    double _dragStartLeft;
    double _dragStartTop;
    double _dragScale = 1;

    public event Action? QuitRequested;

    /// <summary>Set by Shell: re-registers the dock's Ctrl+Alt+1–9 snippet hotkeys.</summary>
    public Action? ApplySnippetHotkeys { get; set; }

    public FlyoutWindow(CallEngine engine)
    {
        _engine = engine;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 292 + ShadowMargin * 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif");

        _statusOverrideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusOverrideTimer.Tick += (_, _) =>
        {
            _statusOverrideTimer.Stop();
            _statusOverride = null;
            SyncFromEngine();
        };

        _mainPanel = BuildMainPanel();
        _welcomePanel = BuildWelcomePanel();
        _softphonePanel = BuildSoftphonePanel();
        _snippetsPanel = BuildSnippetsPanel();
        _remindersPanel = BuildRemindersPanel();
        _notesPanel = BuildNotesPanel();
        _statsPanel = BuildStatsPanel();
        _commandsPanel = BuildCommandsPanel();
        _bridgetPanel = BuildBridgetPanel();
        // The outer scroll host is what keeps a clamped-height flyout usable:
        // when a panel is taller than the screen the content scrolls.
        var host = new Grid();
        _panelHost = host;
        host.Children.Add(_mainPanel);
        host.Children.Add(_welcomePanel);
        host.Children.Add(_softphonePanel);
        host.Children.Add(_snippetsPanel);
        host.Children.Add(_remindersPanel);
        host.Children.Add(_notesPanel);
        host.Children.Add(_statsPanel);
        host.Children.Add(_commandsPanel);
        host.Children.Add(_bridgetPanel);
        var scrollHost = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = host,
        };

        // Light hitting the top of the glass; over the content, never clickable,
        // capped so it doesn't wash the title text on tall panels.
        var sheen = new Border
        {
            IsHitTestVisible = false,
            Height = 90,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-15), // reach under _root's padding to its edge
            CornerRadius = new CornerRadius(11, 11, 0, 0),
        };
        sheen.SetResourceReference(Border.BackgroundProperty, "GlassSheenBrush");
        var glassHost = new Grid();
        glassHost.Children.Add(scrollHost);
        glassHost.Children.Add(sheen);

        _root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(ShadowMargin),
            RenderTransform = new TransformGroup { Children = { _rootScale, _rootSlide } },
            RenderTransformOrigin = new Point(0.5, 1),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            Child = glassHost,
        };
        _root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _root.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeBrush");
        Content = _root;

        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };

        // A panel switch can double the height — keep the card anchored and
        // fully on-screen (unless the user has dragged it somewhere).
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_userMoved && !_hiding && !_morphing)
                Dispatcher.InvokeAsync(() => PositionNearTray(initial: false), DispatcherPriority.Loaded);
        };
        _root.PreviewMouseLeftButtonDown += OnRootDragStart;
        _root.PreviewMouseMove += OnRootDragMove;
        _root.PreviewMouseLeftButtonUp += OnRootDragEnd;
    }

    // ---- panels ----------------------------------------------------------

    /// <summary>The quiet what-are-my-hotkeys line under the main panel's links.</summary>
    void UpdateHotkeyCaption()
    {
        var ask = Hotkey.LoadAssistant();
        var dictate = Hotkey.LoadDictation();
        var parts = new List<string>(2);
        if (!ask.IsOff) parts.Add($"{ask} — ask Bridget");
        if (!dictate.IsOff) parts.Add($"{dictate} — dictate");
        _hotkeyCaption.Text = string.Join("   ·   ", parts);
        _hotkeyCaption.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Top-of-panel return affordance — panels can be taller than the
    /// screen now, and the Done button lives at the (scrolled) bottom.</summary>
    TextBlock BackLink()
    {
        var back = Ui.Link("‹ Back", 10.5);
        back.Margin = new Thickness(0, 0, 0, 8);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        return back;
    }

    StackPanel BuildMainPanel()
    {
        var panel = new StackPanel();

        _statusHalo = new Ellipse
        {
            Width = 8,
            Height = 8,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _haloScale = new ScaleTransform(1, 1);
        _statusHalo.RenderTransform = _haloScale;
        _statusHalo.SetResourceReference(Shape.FillProperty, "AmberBrush");

        _statusDot = new Ellipse { Width = 8, Height = 8 };
        _statusDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");

        var dotHost = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        dotHost.Children.Add(_statusHalo);
        dotHost.Children.Add(_statusDot);

        _statusText = Ui.Text("Listening for calls", 13.5, "TextPrimaryBrush", FontWeights.SemiBold);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.Margin = new Thickness(8, 0, 0, 0);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(dotHost);
        statusRow.Children.Add(_statusText);
        panel.Children.Add(statusRow);

        _statsLine = Ui.Text("", 10.5, "TextSecondaryBrush");
        _statsLine.Margin = new Thickness(20, 4, 0, 0); // aligns under the status text
        _statsLine.Visibility = Visibility.Collapsed;
        panel.Children.Add(_statsLine);

        panel.Children.Add(Ui.Divider(12, 12));

        panel.Children.Add(Ui.Text("Detect calls by", 11, "TextSecondaryBrush"));

        _segmented = new Segmented("Microphone", "Call events", (int)_engine.Mode)
        {
            Margin = new Thickness(0, 8, 0, 0),
        };
        _segmented.SelectionChanged += index =>
        {
            _engine.SetMode((TriggerMode)index);
            UpdateModeCaption();
        };
        panel.Children.Add(_segmented);

        _caption = Ui.Text("", 11, "TextSecondaryBrush");
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.Margin = new Thickness(2, 8, 2, 0);
        panel.Children.Add(_caption);

        _setupLink = Ui.Link("Set up Softphone.Pro…", 11);
        _setupLink.Margin = new Thickness(2, 6, 2, 0);
        _setupLink.MouseLeftButtonUp += (_, _) => ShowSoftphoneSetup();
        panel.Children.Add(_setupLink);

        panel.Children.Add(Ui.Divider(12, 12));

        _switchEnabled = new PillSwitch(_engine.Enabled);
        _switchEnabled.Toggled += on => _engine.SetEnabled(on);
        panel.Children.Add(Ui.ToggleRow("Pause music during calls", _switchEnabled));

        _switchStartup = new PillSwitch(Settings.StartWithWindows);
        _switchStartup.Toggled += on => Settings.StartWithWindows = on;
        var startupRow = Ui.ToggleRow("Start with Windows", _switchStartup);
        startupRow.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(startupRow);

        // Six destinations read better as a tight two-column grid than a
        // scrolling list of links.
        var linkGrid = new Grid { Margin = new Thickness(2, 12, 2, 0) };
        linkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < 3; r++)
            linkGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddLink(string text, int row, int column, Action onClick)
        {
            var link = Ui.Link(text, 11);
            link.Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 0);
            link.MouseLeftButtonUp += (_, _) => onClick();
            Grid.SetRow(link, row);
            Grid.SetColumn(link, column);
            linkGrid.Children.Add(link);
        }

        AddLink("Ask Bridget…", 0, 0, ShowBridget);
        AddLink("Commands…", 0, 1, ShowCommands);
        AddLink("Snippets…", 1, 0, ShowSnippets);
        AddLink("Reminders…", 1, 1, ShowReminders);
        AddLink("Notes & dictation…", 2, 0, ShowNotes);
        AddLink("Stats…", 2, 1, ShowStats);
        panel.Children.Add(linkGrid);

        _hotkeyCaption = Ui.Text("", 10, "TextSecondaryBrush");
        _hotkeyCaption.TextWrapping = TextWrapping.Wrap;
        _hotkeyCaption.Margin = new Thickness(2, 10, 2, 0);
        panel.Children.Add(_hotkeyCaption);

        panel.Children.Add(Ui.Divider(12, 10));

        var footer = new Grid();
        var appName = Ui.Text($"Bridget {Program.Version}", 10.5, "TextSecondaryBrush");
        appName.VerticalAlignment = VerticalAlignment.Center;
        var quit = Ui.Text("Quit", 11, "TextSecondaryBrush");
        quit.Cursor = Cursors.Hand;
        quit.HorizontalAlignment = HorizontalAlignment.Right;
        quit.MouseEnter += (_, _) => quit.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        quit.MouseLeave += (_, _) => quit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        quit.MouseLeftButtonUp += (_, _) => QuitRequested?.Invoke();
        footer.Children.Add(appName);
        footer.Children.Add(quit);
        panel.Children.Add(footer);

        UpdateModeCaption();
        return panel;
    }

    StackPanel BuildWelcomePanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Welcome to Bridget", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Your music pauses when a call starts and comes back when it ends — and once you're set up, press Ctrl+Alt+B and just ask.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        var question = Ui.Text("How should calls be detected?", 12, "TextPrimaryBrush", FontWeights.SemiBold);
        question.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(question);

        _cardMic = Ui.OptionCard("Microphone", "Pauses whenever any app uses your mic. No setup.");
        _cardMic.Margin = new Thickness(0, 10, 0, 0);
        _cardMic.MouseLeftButtonUp += (_, _) => SelectWelcomeCard(0);
        panel.Children.Add(_cardMic);

        _cardEvents = Ui.OptionCard("Softphone events", "Pauses only on answered calls. Best for outbound sales.");
        _cardEvents.Margin = new Thickness(0, 8, 0, 0);
        _cardEvents.MouseLeftButtonUp += (_, _) => SelectWelcomeCard(1);
        panel.Children.Add(_cardEvents);

        var cont = Ui.PrimaryButton("Continue");
        cont.Margin = new Thickness(0, 14, 0, 0);
        cont.MouseLeftButtonUp += (_, _) => FinishWelcome();
        panel.Children.Add(cont);

        SelectWelcomeCard((int)_engine.Mode);
        return panel;
    }

    StackPanel BuildSoftphonePanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Connect Softphone.Pro", 15, "TextPrimaryBrush", FontWeights.SemiBold));

        _softphoneNotice = Ui.Text("", 11, "AmberBrush", FontWeights.SemiBold);
        _softphoneNotice.TextWrapping = TextWrapping.Wrap;
        _softphoneNotice.Margin = new Thickness(0, 6, 0, 0);
        _softphoneNotice.Visibility = Visibility.Collapsed;
        panel.Children.Add(_softphoneNotice);

        var body = Ui.Text("In Softphone.Pro, open Settings → Integration → Third-party systems and add three handlers:", 11.5, "TextSecondaryBrush");
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(body);

        // Softphone.Pro launches the string as "path + arguments" without
        // shell-style quote stripping, so quote only when unavoidable.
        var exe = Environment.ProcessPath ?? "Bridget.exe";
        var hasSpaces = exe.Contains(' ');
        if (hasSpaces) exe = $"\"{exe}\"";
        var rows = new (string Caption, string Command)[]
        {
            // %NUMBER% is substituted by Softphone.Pro with the caller's
            // number, which tags notes and toasts with who the call was with.
            ("Outgoing call answer", $"{exe} pause %NUMBER%"),
            ("Incoming call answer", $"{exe} pause %NUMBER%"),
            ("Call end", $"{exe} resume"),
        };
        double top = 12;
        foreach (var (caption, command) in rows)
        {
            var row = Ui.CommandRow(caption, command);
            row.Margin = new Thickness(0, top, 0, 0);
            top = 8;
            panel.Children.Add(row);
        }
        if (hasSpaces)
        {
            var spaceHint = Ui.Text("If a handler doesn't fire, move Bridget.exe to a folder without spaces (e.g. C:\\Tools) — some softphones don't handle quoted paths.", 10.5, "TextSecondaryBrush");
            spaceHint.TextWrapping = TextWrapping.Wrap;
            spaceHint.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(spaceHint);
        }

        var hint = Ui.Text("Tip: run “Bridget test” in a terminal — your music pauses for 8 seconds, then resumes.", 11, "TextSecondaryBrush");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(hint);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 14, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        var note = Ui.Text("You can change this anytime from this menu.", 10.5, "TextSecondaryBrush");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(note);

        return panel;
    }

    StackPanel BuildSnippetsPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Text("Snippets", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Hover the dock above the taskbar and click a chip to paste it into the app you're working in. Right-click copies.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        var hotkeySwitch = new PillSwitch(Settings.SnippetHotkeys);
        hotkeySwitch.Toggled += on =>
        {
            Settings.SnippetHotkeys = on;
            ApplySnippetHotkeys?.Invoke();
            RebuildSnippetList(); // show/hide the number badges
        };
        var hotkeyRow = Ui.ToggleRow("Paste with Ctrl+Alt+1–9", hotkeySwitch);
        hotkeyRow.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(hotkeyRow);

        var hotkeyHint = Ui.Text("Global shortcuts — if you type with AltGr, leave this off.", 10.5, "TextSecondaryBrush");
        hotkeyHint.TextWrapping = TextWrapping.Wrap;
        hotkeyHint.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(hotkeyHint);

        _snippetList = new StackPanel();
        var scroll = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _snippetList,
            Margin = new Thickness(0, 2, 0, 0),
        };
        panel.Children.Add(scroll);

        _addSnippetLink = Ui.Link("+ Add snippet", 11.5);
        _addSnippetLink.Margin = new Thickness(2, 10, 2, 0);
        _addSnippetLink.MouseLeftButtonUp += (_, _) =>
        {
            if (_snippets.Count >= SnippetStore.MaxSnippets) return;
            _snippets.Add(new Snippet("", ""));
            _editingIndex = _snippets.Count - 1;
            RebuildSnippetList();
        };
        panel.Children.Add(_addSnippetLink);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 12, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        return panel;
    }

    void RebuildSnippetList()
    {
        _snippetList.Children.Clear();
        for (var i = 0; i < _snippets.Count; i++)
            _snippetList.Children.Add(BuildSnippetCard(i));
        _addSnippetLink.Visibility =
            _snippets.Count >= SnippetStore.MaxSnippets ? Visibility.Collapsed : Visibility.Visible;
    }

    Border BuildSnippetCard(int index)
    {
        var snippet = _snippets[index];
        var stack = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 3; c++)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = snippet.Label.Length > 0 ? snippet.Label : "(untitled)";
        if (Settings.SnippetHotkeys && index < 9) titleText = $"{index + 1} · {titleText}";
        var title = Ui.Text(titleText, 12, "TextPrimaryBrush", FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);

        TextBlock Link(string text, int column, Action onClick, bool enabled = true)
        {
            var link = Ui.Text(text, 11, enabled ? "AccentBrush" : "TextSecondaryBrush", FontWeights.SemiBold);
            link.Margin = new Thickness(10, 0, 0, 0);
            link.VerticalAlignment = VerticalAlignment.Center;
            if (enabled)
            {
                link.Cursor = Cursors.Hand;
                link.MouseLeftButtonUp += (_, _) => onClick();
            }
            Grid.SetColumn(link, column);
            header.Children.Add(link);
            return link;
        }

        Link("▲", 1, () => MoveSnippet(index, -1), enabled: index > 0);
        Link("▼", 2, () => MoveSnippet(index, +1), enabled: index < _snippets.Count - 1);
        if (_editingIndex != index)
            Link("Edit", 3, () =>
            {
                _editingIndex = index;
                RebuildSnippetList();
            });
        stack.Children.Add(header);

        if (_editingIndex == index)
        {
            var labelBox = Ui.TextBox(snippet.Label);
            labelBox.MaxLength = SnippetStore.MaxLabelLength;
            labelBox.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(labelBox);
            var textBox = Ui.TextBox(snippet.Text, multiline: true);
            textBox.MaxLength = SnippetStore.MaxTextLength;
            textBox.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(textBox);

            var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var delete = Ui.Text("Delete", 11, "TextSecondaryBrush", FontWeights.SemiBold);
            delete.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            delete.Cursor = Cursors.Hand;
            delete.MouseLeftButtonUp += (_, _) =>
            {
                _snippets.RemoveAt(index);
                _editingIndex = -1;
                CommitSnippets();
            };
            buttons.Children.Add(delete);

            var cancel = Ui.Text("Cancel", 11, "TextSecondaryBrush", FontWeights.SemiBold);
            cancel.Cursor = Cursors.Hand;
            cancel.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(cancel, 2);
            cancel.MouseLeftButtonUp += (_, _) =>
            {
                if (snippet.Label.Length == 0 && snippet.Text.Length == 0) _snippets.RemoveAt(index);
                _editingIndex = -1;
                RebuildSnippetList();
            };
            buttons.Children.Add(cancel);

            var save = Ui.Text("Save", 11, "AccentBrush", FontWeights.SemiBold);
            save.Cursor = Cursors.Hand;
            Grid.SetColumn(save, 3);
            save.MouseLeftButtonUp += (_, _) =>
            {
                _snippets[index] = new Snippet(labelBox.Text.Trim(), textBox.Text);
                _editingIndex = -1;
                CommitSnippets();
            };
            buttons.Children.Add(save);

            stack.Children.Add(buttons);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return card;
    }

    void MoveSnippet(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _snippets.Count) return;
        (_snippets[index], _snippets[target]) = (_snippets[target], _snippets[index]);
        _editingIndex = -1;
        CommitSnippets();
    }

    void CommitSnippets()
    {
        // On a failed write keep the in-memory edit visible for the session
        // instead of silently reverting to the on-disk state.
        if (SnippetStore.Save(_snippets)) _snippets = SnippetStore.Load();
        RebuildSnippetList();
    }

    public void ShowSnippets()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowSnippets);
            return;
        }
        _snippets = SnippetStore.Load();
        _editingIndex = -1;
        RebuildSnippetList();
        Ui.StaggerIn(_snippetList);
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_snippetsPanel);
    }

    public void ShowSoftphoneSetup(string? notice = null)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowSoftphoneSetup(notice));
            return;
        }
        _softphoneNotice.Text = notice ?? "";
        _softphoneNotice.Visibility = string.IsNullOrEmpty(notice) ? Visibility.Collapsed : Visibility.Visible;
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_softphonePanel);
    }

    void SelectWelcomeCard(int index)
    {
        _welcomeSelection = index;
        Ui.SetCardSelected(_cardMic, index == 0);
        Ui.SetCardSelected(_cardEvents, index == 1);
    }

    void FinishWelcome()
    {
        Settings.OnboardingDone = true;
        var mode = _welcomeSelection == 1 ? TriggerMode.SoftphoneEvents : TriggerMode.Microphone;
        _engine.SetMode(mode);
        _segmented.Select((int)mode, animate: false);
        UpdateModeCaption();
        if (mode == TriggerMode.SoftphoneEvents)
        {
            ShowPanel(_softphonePanel);
        }
        else
        {
            ShowPanel(_mainPanel);
            ShowTransientStatus("You're all set.");
        }
    }

    void UpdateModeCaption()
    {
        bool events = _engine.Mode == TriggerMode.SoftphoneEvents;
        _caption.Text = events
            ? "Pauses only when your softphone reports an answered call. Ringing never interrupts the music."
            : "Pauses whenever any app starts using your microphone. No setup needed.";
        _setupLink.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowPanel(UIElement panel)
    {
        // _root.Child is the ScrollViewer since v6 — casting it to Grid threw
        // on every open and silently killed the whole flyout.
        var host = _panelHost;
        UIElement? current = null;
        foreach (UIElement child in host.Children)
            if (child.Visibility == Visibility.Visible && child != panel) current = child;

        if (!IsVisible || current is null)
        {
            // Window hidden (or already on this panel): switch instantly.
            foreach (UIElement child in host.Children)
            {
                child.BeginAnimation(OpacityProperty, null);
                child.Opacity = 1;
                child.Visibility = child == panel ? Visibility.Visible : Visibility.Collapsed;
            }
            return;
        }

        // Cross-fade: outgoing dips out, incoming rises in, and the window
        // glides to the incoming panel's height instead of snapping.
        var targetPanelHeight = MeasurePanelHeight(panel);
        var outgoing = current;
        var fadeOut = Motion.Fade(0, 90);
        fadeOut.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.BeginAnimation(OpacityProperty, null);
            outgoing.Opacity = 1;

            panel.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            var rise = new TranslateTransform(0, 6);
            panel.RenderTransform = rise;
            panel.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, Motion.Base));
            rise.BeginAnimation(TranslateTransform.YProperty, Motion.Fade(0, Motion.Base));
            MorphHeightTo(targetPanelHeight);
        };
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Measures what a (collapsed) panel would want at content width.
    /// The visibility flip is synchronous — no layout pass sees it.</summary>
    double MeasurePanelHeight(UIElement panel)
    {
        var width = _panelHost.ActualWidth > 0 ? _panelHost.ActualWidth : Width - ShadowMargin * 2 - 32;
        var was = panel.Visibility;
        panel.Visibility = Visibility.Hidden; // collapsed elements measure to zero
        panel.Measure(new Size(width, double.PositiveInfinity));
        var height = panel.DesiredSize.Height;
        panel.Visibility = was;
        return height;
    }

    /// <summary>Animates the window between panel heights, keeping the bottom
    /// edge planted, then hands sizing back to SizeToContent.</summary>
    void MorphHeightTo(double panelHeight)
    {
        var chrome = ActualHeight - _panelHost.ActualHeight;
        if (double.IsNaN(chrome) || chrome <= 0) return;
        var target = Math.Min(chrome + panelHeight, MaxHeight);
        if (Math.Abs(target - ActualHeight) < 2) return;

        _morphing = true;
        SizeToContent = SizeToContent.Manual;
        var oldTop = Top;
        var newTop = oldTop + (ActualHeight - target); // bottom edge stays put

        var heightAnim = Motion.FromTo(ActualHeight, target, Motion.Slow, Motion.Out);
        heightAnim.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            BeginAnimation(TopProperty, null);
            Height = target;
            Top = newTop;
            SizeToContent = SizeToContent.Height;
            _morphing = false;
            if (!_userMoved) Dispatcher.InvokeAsync(() => PositionNearTray(initial: false), DispatcherPriority.Loaded);
        };
        BeginAnimation(TopProperty, Motion.FromTo(oldTop, newTop, Motion.Slow, Motion.Out));
        BeginAnimation(HeightProperty, heightAnim);
    }

    void ShowTransientStatus(string text)
    {
        _statusOverride = text;
        _statusText.Text = text;
        _statusOverrideTimer.Stop();
        _statusOverrideTimer.Start();
    }

    // ---- state sync ------------------------------------------------------

    public void SyncFromEngine()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(SyncFromEngine);
            return;
        }

        var state = _engine.State;
        _statusDot.SetResourceReference(Shape.FillProperty, state switch
        {
            CallState.OnCall => "AmberBrush",
            CallState.Disabled => "TextSecondaryBrush",
            _ => "StatusGoodBrush",
        });
        if (_statusOverride is null)
        {
            _statusText.Text = state switch
            {
                CallState.OnCall => "On a call — music paused",
                CallState.Disabled => "Paused",
                _ => "Listening for calls",
            };
        }
        _switchEnabled.Set(_engine.Enabled, animate: false);
        UpdatePulse();
    }

    void UpdatePulse()
    {
        bool shouldPulse = _engine.State == CallState.OnCall && IsVisible;
        if (shouldPulse == _pulsing) return;
        _pulsing = shouldPulse;

        if (shouldPulse)
        {
            var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromSeconds(1.2))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _statusHalo.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _statusHalo.BeginAnimation(OpacityProperty, null);
            _haloScale.ScaleX = _haloScale.ScaleY = 1;
            _statusHalo.Opacity = 0;
        }
    }

    // ---- show / hide -----------------------------------------------------

    public void ShowFlyout(bool onboarding = false) => ShowFlyoutCore(onboarding, force: false);

    void ShowFlyoutCore(bool onboarding, bool force)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowFlyoutCore(onboarding, force));
            return;
        }
        UpdateStatsLine(); // recompute "Today:" — the day may have rolled over
        UpdateHotkeyCaption();
        if (IsVisible)
        {
            if (_hiding)
            {
                // Cancel the in-flight hide: replacing the animation removes
                // its Completed callback, so the pending Hide() never runs.
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                _hiding = false;
            }
            // Already open (e.g. the user clicked the tray before the
            // first-run timer fired): still surface the requested panel.
            if (onboarding) ShowPanel(_welcomePanel);
            Activate();
            return;
        }
        // Clicking the tray icon while open fires Deactivated (hide) then
        // MouseUp (show) — without this guard the flyout flickers reopen.
        // Explicit requests (onboarding, dock chips) are never swallowed by it.
        if (!onboarding && !force && (DateTime.UtcNow - _lastHiddenAt).TotalMilliseconds < 250) return;

        ShowPanel(onboarding ? _welcomePanel : _mainPanel);
        SyncFromEngine();

        _anchor = WinF.Cursor.Position;
        _userMoved = false;
        Opacity = 0;
        Show();
        Dispatcher.InvokeAsync(() =>
        {
            PositionNearTray();
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            _rootSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            // The glass springs up as it fades in.
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.97, 1, 220, Motion.Overshoot));
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.97, 1, 220, Motion.Overshoot));
            Activate();
            UpdatePulse();
        }, DispatcherPriority.Loaded);
    }

    void HideFlyout()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            _rootSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _rootSlide.Y = 0;
            _hiding = false;
            _userMoved = false; // next open re-anchors near the tray
            _lastHiddenAt = DateTime.UtcNow;
            UpdatePulse();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    void PositionNearTray(bool initial = true)
    {
        var screen = WinF.Screen.FromPoint(_anchor);
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;

        // Repositions while visible must not park the window at the monitor
        // center first (Dpi.MoveToAndGetScale does) — that would flash.
        var scale = initial ? Dpi.MoveToAndGetScale(this, wa) : CurrentScale();

        // Never taller than the work area — the panel host scrolls instead.
        MaxHeight = wa.Height / scale + (ShadowMargin - EdgeGap) * 2;

        UpdateLayout();
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double overlap = (ShadowMargin - EdgeGap) * scale; // window may overhang the work area by the shadow

        bool topBar = wa.Top > bounds.Top;
        bool leftBar = wa.Left > bounds.Left;
        bool rightBar = wa.Right < bounds.Right;

        double left = _anchor.X - w / 2;
        double top;
        if (topBar) top = wa.Top - overlap;
        else if (leftBar) { left = wa.Left - overlap; top = _anchor.Y - h / 2; }
        else if (rightBar) { left = wa.Right - w + overlap; top = _anchor.Y - h / 2; }
        else top = wa.Bottom - h + overlap; // bottom taskbar (default)

        // Clamp with the top/left bound LAST: if the window ever ends up
        // taller than the work area, the top edge must stay reachable.
        left = Math.Max(Math.Min(left, wa.Right - w + overlap), wa.Left - overlap);
        top = Math.Max(Math.Min(top, wa.Bottom - h + overlap), wa.Top - overlap);

        Left = left / scale;
        Top = top / scale;
    }

    double CurrentScale()
    {
        var scale = NativeMethods.GetDpiForWindow(new WindowInteropHelper(this).EnsureHandle()) / 96.0;
        return scale <= 0 ? 1 : scale;
    }

    // ---- drag ------------------------------------------------------------
    // The whole card is a drag surface (threshold keeps plain clicks
    // working; text boxes and scrollbars are exempt). Once moved, the
    // flyout stays where the user put it until it's hidden.

    void OnRootDragStart(object sender, MouseButtonEventArgs e)
    {
        if (InsideTextInput(e.OriginalSource as DependencyObject)) return;
        NativeMethods.GetCursorPos(out _dragStartPt);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragScale = CurrentScale();
        _dragArmed = true;
        _dragging = false;
    }

    void OnRootDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed = false;
            return;
        }
        NativeMethods.GetCursorPos(out var pt);
        var dx = pt.X - _dragStartPt.X;
        var dy = pt.Y - _dragStartPt.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(dy) <= SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            _userMoved = true; // auto-reposition stands down until hidden
            _root.CaptureMouse();
        }
        Left = _dragStartLeft + dx / _dragScale;
        Top = _dragStartTop + dy / _dragScale;
    }

    void OnRootDragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (!_dragging) return;
        _dragging = false;
        _root.ReleaseMouseCapture();
        e.Handled = true; // the release must not click whatever is underneath
    }

    static bool InsideTextInput(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
                or System.Windows.Controls.Primitives.ScrollBar) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
