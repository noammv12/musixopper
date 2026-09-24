using System.Windows;
using System.Windows.Media;

namespace Palon.UI;

/// <summary>What Palon is doing — the avatar's whole emotional range.</summary>
enum PalonMood { Idle, Listen, Think, Talk, Happy }

/// <summary>
/// Palon, drawn live: a dark glossy sphere whose cut-out blue eyes do the
/// acting. Size it with Width/Height (square); set Mood and it eases there.
/// This is the shared contract the Terminal and the dock build against —
/// the frame engine lands behind it without changing the surface.
/// </summary>
sealed class PalonAvatar : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(PalonMood), typeof(PalonAvatar),
        new FrameworkPropertyMetadata(PalonMood.Idle, FrameworkPropertyMetadataOptions.AffectsRender));

    public PalonMood Mood
    {
        get => (PalonMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>Plays the happy hop once, then settles back to the current mood.</summary>
    public void Cheer() { }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        if (r <= 0) return;
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1D)), null, new Point(ActualWidth / 2, ActualHeight / 2), r * 0.8, r * 0.8);
    }
}
