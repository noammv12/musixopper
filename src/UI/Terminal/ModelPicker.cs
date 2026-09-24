using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// "Gemini 3.8 Flash ▾": the model pill (Ask header, menu bar) and its
/// glass popover. The choice persists in Settings and applies to the next
/// AI request anywhere (Ask, summaries, follow-ups, recap); a run already
/// in flight keeps its pinned provider.
/// </summary>
static class ModelPicker
{
    /// <summary>Raised after the choice or a key changes, so every pill re-labels.</summary>
    public static event Action? Changed;

    public static void Choose(string id)
    {
        Settings.AiModelChoice = id;
        Changed?.Invoke();
    }

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>The pill. compact = menu-bar size.</summary>
    public static Border Pill(TerminalWindow host, bool compact)
    {
        var size = compact ? 12 : 13;
        var label = Kit.T("", size, compact ? Tone.MutedSoft : Tone.TextSoft, FontWeights.Medium);
        label.FlowDirection = FlowDirection.LeftToRight;
        var dot = Kit.Dot(Tone.Green, 6);
        var chevron = Kit.T("▾", size - 1, Tone.Muted);
        var row = Kit.Row(6, dot, label, chevron);
        row.FlowDirection = FlowDirection.LeftToRight;
        row.VerticalAlignment = VerticalAlignment.Center;
        var pill = new Border
        {
            Height = compact ? 24 : 30,
            CornerRadius = new CornerRadius(compact ? 12 : 15),
            Padding = new Thickness(compact ? 10 : 12, 0, compact ? 10 : 12, 0),
            BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1),
            Child = row,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "בחירת מודל",
        };
        void Sync()
        {
            label.Text = AiModels.Find(Settings.AiModelChoice).Label;
            dot.Background = AiChat.HasKey ? Tone.Green : Tone.Dim;
        }
        Sync();
        Action handler = () => pill.Dispatcher.BeginInvoke(Sync);
        pill.Loaded += (_, _) => { Changed -= handler; Changed += handler; Sync(); };
        pill.Unloaded += (_, _) => Changed -= handler;
        Kit.HoverFill(pill, Tone.FillSoft, Tone.Fill);
        Kit.Press(pill);
        Kit.Clickable(pill, () => Open(host, pill));
        return pill;
    }

    static void Open(TerminalWindow host, FrameworkElement anchor)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 8,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };
        var card = new Border
        {
            Width = 320,
            CornerRadius = new CornerRadius(20),
            Background = Tone.GlassDeep,
            BorderBrush = Tone.GlassDeepRim,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(18, 0, 18, 28), // room for the shadow
            FlowDirection = FlowDirection.RightToLeft,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.6,
            },
        };
        var list = new StackPanel();
        var head = Kit.T("המוח של Palon", 12, Tone.Muted);
        head.Margin = new Thickness(12, 6, 12, 6);
        list.Children.Add(head);

        var hasGemini = Settings.GeminiKey is not null;
        var hasDeepSeek = Settings.DeepSeekKey is not null;
        var current = AiModels.Find(Settings.AiModelChoice).Id;
        var rows = new List<FrameworkElement>();
        foreach (var option in AiModels.Catalog)
        {
            var available = AiModels.IsAvailable(option, hasGemini, hasDeepSeek);
            var active = option.Id == current;
            var name = Kit.T(option.Label, 14, available ? Tone.Text : Tone.Faint, FontWeights.Medium);
            var provider = Kit.T(option.Provider, 11, Tone.Muted);
            provider.FlowDirection = FlowDirection.LeftToRight;
            provider.Margin = new Thickness(8, 0, 0, 0);
            var titleLine = Kit.Row(0, name, provider);
            var hint = Kit.T(available ? option.Hint : "חסר מפתח · הוסף בהגדרות", 12, available ? Tone.MutedSoft : Tone.AmberText);
            hint.Margin = new Thickness(0, 2, 0, 0);
            var text = Kit.Col(0, titleLine, hint);
            var check = Kit.Icon(Icons.Check, 16, Tone.Accent, 2);
            check.Opacity = active ? 1 : 0;
            var bar = Kit.Bar(text, check);
            var row = Kit.ListRow(bar, new Thickness(12, 9, 12, 9));
            row.Background = active ? Tone.FillSoft : row.Background;
            row.Focusable = true;
            row.FocusVisualStyle = null;
            row.Cursor = Cursors.Hand;
            if (!available) row.Opacity = 0.6;
            var id = option.Id;
            Kit.Clickable(row, () =>
            {
                popup.IsOpen = false;
                if (!available)
                {
                    BrainSheet.Show(host);
                    return;
                }
                Choose(id);
                host.Toast($"Palon עובר ל-{AiModels.Find(id).Label}");
            });
            Kit.Press(row, 0.98);
            list.Children.Add(row);
            rows.Add(row);
        }

        var sep = new Border { Height = 1, Background = Tone.Hairline, Margin = new Thickness(8, 6, 8, 6) };
        list.Children.Add(sep);
        var settings = Kit.ListRow(Kit.T("מפתחות והגדרות…", 13, Tone.TextSoft), new Thickness(12, 9, 12, 9));
        settings.Cursor = Cursors.Hand;
        Kit.Clickable(settings, () => { popup.IsOpen = false; BrainSheet.Show(host); });
        list.Children.Add(settings);

        card.Child = list;
        popup.Child = card;
        popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) popup.IsOpen = false; };
        popup.IsOpen = true;
        Kit.Rise(rows);
    }
}
