using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Palon.Agent;
using Palon.Notes;

namespace Palon.Vision;

/// <summary>An approved capture: the exact JPEG the user saw in the gate.</summary>
sealed record ScreenShot(byte[] Jpeg, int Width, int Height, string? WindowTitle);

/// <summary>A finished screen read: the answer for the user and the verbatim text for follow-ups.</summary>
sealed record ScreenRead(string Answer, string Transcript);

/// <summary>
/// Screen reading on request. Every path starts with a user action (Ask,
/// the dock chip, the opt-in hotkey, or the agent tool the user's own
/// question invoked), then: Palon's own windows step aside, the screen is
/// frozen, the user frames a region or clicks a window, the exact cropped
/// JPEG is shown in the privacy gate, and only after "Send" does anything
/// leave the machine. All of it runs on the UI thread.
/// </summary>
static class ScreenReader
{
    const int JpegQuality = 85;
    static bool _busy;

    public const string NoVisionModel =
        "המודל הנוכחי לא קורא תמונות. בחר Gemini או DeepSeek Flash במתג המודל ונסה שוב.";

    /// <summary>Capture → pick → gate. Null when the user cancelled at any step (reason says why, for logs/UI).</summary>
    public static async Task<(ScreenShot? Shot, string? Error)> CaptureAsync(bool windowMode, string purpose)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            return await d.InvokeAsync(() => CaptureAsync(windowMode, purpose)).Task.Unwrap();
        if (_busy) return (null, "כבר בוחר אזור במסך");
        var target = AiChat.VisionTarget();
        if (target is null) return (null, NoVisionModel);

        _busy = true;
        var hidden = HidePalon();
        try
        {
            if (hidden.Count > 0) await Task.Delay(220); // let DWM repaint what was under us
            var vs = CaptureNative.VirtualScreen();
            if (vs.IsEmpty) return (null, "לא הצלחתי לצלם את המסך");
            var hbmp = CaptureNative.CaptureVirtualScreen(vs);
            BitmapSource frozen;
            try
            {
                frozen = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                frozen.Freeze();
            }
            finally
            {
                CaptureNative.DeleteObject(hbmp);
            }

            var picker = new RegionPicker(frozen, vs, CaptureNative.WindowsInZOrder(), windowMode);
            var picked = await picker.PickAsync(CaptureNative.Monitors());
            if (picked is null) return (null, null);

            var crop = RegionMath.ToBitmap(picked.Bounds, vs.X, vs.Y, frozen.PixelWidth, frozen.PixelHeight);
            if (!RegionMath.IsUsable(crop)) return (null, "האזור קטן מדי");
            var (jpeg, w, h) = Encode(frozen, crop);
            if (!await SendGate.ConfirmAsync(jpeg, w, h, target, purpose)) return (null, null);
            return (new ScreenShot(jpeg, w, h, picked.WindowTitle), null);
        }
        catch (Exception ex)
        {
            Log.Write($"Screen capture failed: {ex}");
            return (null, "צילום המסך נכשל — ראה לוג");
        }
        finally
        {
            Restore(hidden);
            _busy = false;
        }
    }

    /// <summary>Capture, then ask the model about it. The transcript is remembered for follow-up turns.</summary>
    public static async Task<(ScreenRead? Read, string? Error)> ReadAsync(string? question, bool windowMode, CancellationToken ct)
    {
        var (shot, error) = await CaptureAsync(windowMode, "Palon יקרא את מה שבתמונה ויענה על השאלה שלך.");
        if (shot is null) return (null, error);
        return await ReadShotAsync(shot, question, ct);
    }

    public static async Task<(ScreenRead? Read, string? Error)> ReadShotAsync(ScreenShot shot, string? question, CancellationToken ct)
    {
        var ask = string.IsNullOrWhiteSpace(question) ? "מה כתוב פה? תן תקציר קצר של מה שרואים." : question!.Trim();
        if (shot.WindowTitle is { Length: > 0 } title) ask += $"\n(The image is the window titled \"{title}\".)";
        var (raw, error) = await AiChat.VisionAsync(VisionPayload.ReadSystemPrompt, ask, shot.Jpeg, 1500, ct);
        if (raw is null) return (null, "לא הצלחתי לקרוא את התמונה: " + error);
        var reply = VisionPayload.ParseRead(raw);
        AssistantSession.RememberScreen(reply.Transcript);
        return (new ScreenRead(reply.Answer, reply.Transcript), null);
    }

    /// <summary>Capture a receipt and extract the deal fields (validated, never saved here).</summary>
    public static async Task<(ReceiptData? Receipt, string? Error)> ReceiptAsync(bool windowMode, CancellationToken ct)
    {
        var (shot, error) = await CaptureAsync(windowMode, "Palon יחלץ מהקבלה שם, סכום ותאריך וימלא עסקה חדשה — לבדיקה שלך לפני שמירה.");
        if (shot is null) return (null, error);
        return await ReceiptFromShotAsync(shot, ct);
    }

    public static async Task<(ReceiptData? Receipt, string? Error)> ReceiptFromShotAsync(ScreenShot shot, CancellationToken ct)
    {
        var (raw, error) = await AiChat.VisionAsync(ReceiptExtraction.SystemPrompt, "Extract the receipt fields.", shot.Jpeg, 1500, ct);
        if (raw is null) return (null, "לא הצלחתי לקרוא את הקבלה: " + error);
        var receipt = ReceiptExtraction.Parse(raw);
        if (receipt is null) return (null, "המודל לא החזיר פרטי קבלה קריאים — נסה לסמן את הקבלה מקרוב יותר.");
        AssistantSession.RememberScreen(receipt.Transcript);
        return (receipt, null);
    }

    /// <summary>Opens the Terminal's New deal sheet pre-filled from the receipt. Never saves.</summary>
    public static void OpenDealSheet(ReceiptData receipt)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(() => OpenDealSheet(receipt));
            return;
        }
        UI.TerminalWindow.ShowSingleton();
        if (UI.TerminalWindow.Instance is { } host)
            UI.Sheets.NewDeal(host, prefill: DealPrefill.FromReceipt(receipt, DateTime.Now));
    }

    /// <summary>The Ask-visible line after a receipt read.</summary>
    public static string ReceiptSummary(ReceiptData receipt) =>
        "פתחתי עסקה חדשה מהקבלה — בדוק ושמור.\n" + ReceiptExtraction.Describe(receipt)
        + (receipt.Warnings.Count > 0 ? "\n" + string.Join("\n", receipt.Warnings) : "");

    // ---- Palon's own windows step aside ------------------------------------------

    static List<Window> HidePalon()
    {
        var hidden = new List<Window>();
        if (Application.Current is null) return hidden;
        foreach (Window w in Application.Current.Windows)
        {
            // The dock is a sliver on the taskbar edge; it stays (and is excluded from window picking).
            if (!w.IsVisible || w is UI.DockWindow) continue;
            w.Hide();
            hidden.Add(w);
        }
        return hidden;
    }

    static void Restore(List<Window> hidden)
    {
        foreach (var w in hidden)
        {
            try
            {
                w.Show();
            }
            catch (InvalidOperationException)
            {
                // closed meanwhile
            }
        }
        hidden.LastOrDefault(w => w is UI.TerminalWindow)?.Activate();
    }

    // ---- encoding ---------------------------------------------------------------------

    /// <summary>Crop → downscale (FitWithin) → JPEG q85; re-encodes smaller if still over the byte cap.</summary>
    internal static (byte[] Jpeg, int Width, int Height) Encode(BitmapSource frozen, PxRect crop)
    {
        BitmapSource cropped = new CroppedBitmap(frozen, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        var attempts = new (int Edge, int Quality)[] { (2000, JpegQuality), (1600, 80), (1200, 70) };
        byte[] bytes = Array.Empty<byte>();
        int outW = crop.Width, outH = crop.Height;
        foreach (var (edge, quality) in attempts)
        {
            (outW, outH) = RegionMath.FitWithin(crop.Width, crop.Height, edge);
            BitmapSource scaled = outW == crop.Width && outH == crop.Height
                ? cropped
                : new TransformedBitmap(cropped, new ScaleTransform((double)outW / crop.Width, (double)outH / crop.Height));
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            bytes = ms.ToArray();
            if (bytes.Length <= VisionPayload.MaxImageBytes) break;
        }
        return (bytes, outW, outH);
    }
}
