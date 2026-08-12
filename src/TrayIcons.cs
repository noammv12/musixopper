using System.Drawing.Drawing2D;

namespace Musixopper;

/// <summary>Tray icons drawn at runtime so no .ico assets need shipping.</summary>
static class TrayIcons
{
    public static readonly Icon Idle = Make(Color.FromArgb(0x2E, 0xB8, 0x6E));     // green: watching
    public static readonly Icon OnCall = Make(Color.FromArgb(0xE8, 0x9C, 0x2C));   // orange: music paused
    public static readonly Icon Disabled = Make(Color.FromArgb(0x8A, 0x8F, 0x98)); // grey: disabled

    static Icon Make(Color background)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(background);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI Symbol", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("♪", font, Brushes.White, new RectangleF(0, 0, 32, 31), format);
        }
        // The HICON is intentionally never destroyed: these three icons live
        // for the whole process lifetime.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
