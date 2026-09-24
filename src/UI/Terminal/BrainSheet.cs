using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// "Palon's brain": the rep's name, the Gemini and DeepSeek keys (same
/// DPAPI-protected Settings the flyout writes) with a Test per key, and the
/// model choice. Reached from the menu-bar Palon label, the gear, and the
/// model popover.
/// </summary>
static class BrainSheet
{
    public static void Show(TerminalWindow host)
    {
        Action close = () => { };
        var body = new StackPanel();

        var title = Kit.T("המוח של Palon", Display.Sheet, Tone.Text, FontWeights.SemiBold);
        var sub = Kit.T("מפתחות, מודל ושם — נשמר מוצפן במחשב הזה בלבד", 13.5, Tone.MutedSoft, wrap: true);
        sub.Margin = new Thickness(0, 3, 0, 0);
        var x = Kit.IconButton(Icons.Close, 34, () => close());
        x.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(Kit.Bar(Kit.Col(0, title, sub), x));
        var (content, name) = Content(host, () => close());
        body.Children.Add(content);

        close = host.ShowSheet(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 520);
        name.Focus();
    }

    /// <summary>The brain's fields (name, keys, model) and a Save — shared by the sheet and
    /// the Settings side sheet. <paramref name="afterSave"/> runs after the toast.</summary>
    public static (FrameworkElement Body, TextBox Name) Content(TerminalWindow host, Action? afterSave)
    {
        var body = new StackPanel();
        var (nameFrame, name) = Kit.LabeledField("השם שלך (לברכה ב״עכשיו״)", "לדוגמה: דני");
        name.Text = Settings.RepName ?? "";
        nameFrame.Margin = new Thickness(0, 22, 0, 0);
        body.Children.Add(nameFrame);

        body.Children.Add(Caption("Gemini · ראשי, שכבה חינמית"));
        var gemini = KeyRow(host, AiModels.Gemini, () => Settings.GeminiKey, k => Settings.GeminiKey = k);
        body.Children.Add(gemini.Row);
        body.Children.Add(gemini.Status);

        body.Children.Add(Caption("DeepSeek · גיבוי"));
        var deepSeek = KeyRow(host, AiModels.DeepSeek, () => Settings.DeepSeekKey, k => Settings.DeepSeekKey = k);
        body.Children.Add(deepSeek.Row);
        body.Children.Add(deepSeek.Status);

        body.Children.Add(Caption("מודל"));
        var picker = ModelPicker.Pill(host, compact: false);
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        picker.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(picker);

        var save = Kit.Pill("שמור", PillKind.Primary, () =>
        {
            Settings.RepName = name.Text;
            gemini.Commit();
            deepSeek.Commit();
            ModelPicker.NotifyChanged();
            host.Refresh();
            host.Toast("נשמר");
            afterSave?.Invoke();
        }, height: 46, fontSize: 15);
        save.Margin = new Thickness(0, 26, 0, 0);
        body.Children.Add(save);
        return (body, name);
    }

    static TextBlock Caption(string text)
    {
        var t = Kit.T(text, 12, Tone.Muted);
        t.Margin = new Thickness(0, 20, 0, 8);
        return t;
    }

    sealed record KeyUi(Grid Row, TextBlock Status, Action Commit);

    static KeyUi KeyRow(TerminalWindow host, string provider, Func<string?> get, Action<string?> set)
    {
        var box = new PasswordBox
        {
            Background = Tone.B("#00000000"), BorderThickness = new Thickness(0), Foreground = Tone.Text,
            CaretBrush = Tone.Text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight, FocusVisualStyle = null,
        };
        var placeholder = Kit.T("", 13.5, Tone.Faint);
        placeholder.FlowDirection = FlowDirection.LeftToRight;
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var layered = new Grid();
        layered.Children.Add(placeholder);
        layered.Children.Add(box);
        var frame = new Border
        {
            Height = 44, CornerRadius = new CornerRadius(22), Background = Tone.FillSoft, BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 0, 16, 0), Child = layered, Cursor = Cursors.IBeam,
        };
        frame.MouseLeftButtonDown += (_, _) => box.Focus();
        box.GotKeyboardFocus += (_, _) => frame.BorderBrush = Tone.AccentLine;
        box.LostKeyboardFocus += (_, _) => frame.BorderBrush = Tone.Hairline;

        var status = Kit.T("", 12.5, Tone.MutedSoft, wrap: true);
        status.Margin = new Thickness(6, 6, 6, 0);
        var removed = false;

        void Paint()
        {
            var has = !removed && get() is not null;
            placeholder.Text = box.Password.Length > 0 ? "" : has ? "•••••••• שמור — הדבק כדי להחליף" : "הדבק מפתח API";
        }
        box.PasswordChanged += (_, _) => { removed = false; Paint(); };

        CancellationTokenSource? cts = null;
        var test = Kit.Pill("בדוק", PillKind.Secondary, async () =>
        {
            var key = box.Password.Trim().Length > 0 ? box.Password.Trim() : removed ? null : get();
            if (key is null)
            {
                status.Foreground = Tone.AmberText;
                status.Text = "אין מפתח לבדוק";
                return;
            }
            cts?.Cancel();
            cts = new CancellationTokenSource();
            status.Foreground = Tone.MutedSoft;
            status.Text = "בודק…";
            try
            {
                var (ok, ms, error) = await AiChat.TestKeyAsync(provider, key, cts.Token);
                status.Foreground = ok ? Tone.GreenText : Tone.RedText;
                status.Text = ok ? $"✓ עובד · {ms} ms" : $"✕ {error}";
                Kit.Pop(status);
            }
            catch (OperationCanceledException) { }
        }, height: 36, fontSize: 13);
        test.Margin = new Thickness(8, 0, 0, 0);
        var remove = Kit.Pill("הסר", PillKind.Ghost, () =>
        {
            removed = true;
            box.Password = "";
            removed = true;
            Paint();
            status.Foreground = Tone.MutedSoft;
            status.Text = "יוסר בשמירה";
        }, height: 36, fontSize: 13);
        remove.Margin = new Thickness(4, 0, 0, 0);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(frame);
        Grid.SetColumn(test, 1);
        row.Children.Add(test);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        Paint();

        void Commit()
        {
            cts?.Cancel();
            if (box.Password.Trim().Length > 0) set(box.Password.Trim());
            else if (removed) set(null);
        }
        return new KeyUi(row, status, Commit);
    }
}
