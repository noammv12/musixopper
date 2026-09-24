using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Palon.UI;

namespace Palon.Vision;

/// <summary>
/// The privacy gate: a small glass card showing exactly the image that
/// would be sent (the encoded JPEG itself, not the live screen), who it
/// goes to, and Send / Cancel. "Always allow for this session" skips the
/// card until Palon restarts — never persisted.
/// </summary>
static class SendGate
{
    /// <summary>Set by the checkbox; process lifetime only, by design.</summary>
    public static bool AllowedForSession { get; private set; }

    public static Task<bool> ConfirmAsync(byte[] jpeg, int width, int height, string target, string purpose)
    {
        if (AllowedForSession) return Task.FromResult(true);
        var done = new TaskCompletionSource<bool>();

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new System.IO.MemoryStream(jpeg);
        image.EndInit();
        image.Freeze();

        var root = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        root.Children.Add(Kit.T("לשלוח את התמונה?", 17, Tone.Text, FontWeights.SemiBold));
        var sub = Kit.T(purpose, 13, Tone.Muted, wrap: true);
        sub.Margin = new Thickness(0, 4, 0, 14);
        root.Children.Add(sub);

        var preview = new Border
        {
            CornerRadius = new CornerRadius(14), BorderBrush = Tone.Hairline, BorderThickness = new Thickness(1),
            Background = Tone.B("#FF0B0B0D"), Padding = new Thickness(6), ClipToBounds = true,
            Child = new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = 360, MaxWidth = 468, FlowDirection = FlowDirection.LeftToRight },
        };
        root.Children.Add(preview);
        var size = Kit.T($"{width} × {height} · {jpeg.Length / 1024} KB", 11.5, Tone.Faint);
        size.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(size);

        var note = Kit.T($"רק האזור הזה נשלח, אל {target}, לקריאה ולתשובה. בשכבה החינמית של Gemini, Google רשאית להשתמש בתוכן ולעבור עליו — אל תשלח פרטים רגישים בלי מפתח בתשלום.",
            12, Tone.MutedSoft, wrap: true);
        note.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(note);

        var always = new CheckBox
        {
            Content = Kit.T("אל תשאל שוב עד שאפעיל מחדש את Palon", 12.5, Tone.Body),
            Margin = new Thickness(0, 12, 0, 0), Foreground = Tone.Body, Cursor = Cursors.Hand,
        };
        root.Children.Add(always);

        Window? window = null;
        void Close(bool send)
        {
            if (done.Task.IsCompleted) return;
            if (send && always.IsChecked == true) AllowedForSession = true;
            done.TrySetResult(send);
            window?.Close();
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var send = Kit.Pill("שלח ל-Palon", PillKind.Primary, () => Close(true), height: 40, fontSize: 14);
        var cancel = Kit.Pill("ביטול", PillKind.Secondary, () => Close(false), height: 40, fontSize: 14);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(send);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var card = Kit.DeepGlass(26, new Thickness(24));
        card.Width = 540;
        card.Child = root;
        card.Margin = new Thickness(30);

        window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = card, Title = "Palon — שליחת תמונה",
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(false);
            else if (e.Key == Key.Enter) Close(true);
        };
        window.Closed += (_, _) => done.TrySetResult(false);
        window.Show();
        window.Activate();
        return done.Task;
    }
}
