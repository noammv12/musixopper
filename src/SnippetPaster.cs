using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Saley.Interop;

namespace Saley;

enum PasteResult
{
    Pasted,
    CopiedOnly,
    Failed,
}

/// <summary>
/// Pastes a snippet into the currently focused app: sets the clipboard,
/// then injects Ctrl+V. Works because the dock never takes activation, so
/// the target app keeps keyboard focus the whole time. The snippet stays
/// on the clipboard afterwards by design (restore-after-paste is racy —
/// the target reads the clipboard asynchronously).
/// Known limit: injected input into elevated (admin) apps is silently
/// blocked by Windows (UIPI); the text is still on the clipboard.
/// </summary>
static class SnippetPaster
{
    const ushort VK_CONTROL = 0x11;
    const ushort VK_V = 0x56;
    static readonly ushort[] PhysicalModifiers =
    {
        0xA0, 0xA1, // left/right Shift
        0xA2, 0xA3, // left/right Ctrl
        0xA4, 0xA5, // left/right Alt
        0x5B, 0x5C, // left/right Win
    };

    public static Task<PasteResult> PasteAsync(Snippet snippet) => PasteTextAsync(snippet.Text, snippet.Label);

    public static async Task<PasteResult> PasteTextAsync(string text, string label = "dictation")
    {
        var target = NativeMethods.GetForegroundWindow();
        if (target == IntPtr.Zero || IsOwnWindow(target))
        {
            if (!TrySetClipboard(text)) return PasteResult.Failed;
            Log.Write($"Copied '{label}' (no paste target)");
            return PasteResult.CopiedOnly;
        }

        if (!TrySetClipboard(text)) return PasteResult.Failed;

        await Task.Delay(50); // let the clipboard settle before the paste lands

        var inputs = new List<NativeMethods.INPUT>();
        // Release any modifiers the user is physically holding, so the
        // injected Ctrl+V can't turn into Ctrl+Shift+V etc.
        var heldModifiers = PhysicalModifiers
            .Where(vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
            .ToList();
        foreach (var vk in heldModifiers) inputs.Add(Key(vk, up: true));
        inputs.Add(Key(VK_CONTROL, up: false));
        inputs.Add(Key(VK_V, up: false));
        inputs.Add(Key(VK_V, up: true));
        inputs.Add(Key(VK_CONTROL, up: true));
        // Re-press what the user is (still) physically holding so the OS
        // keyboard state matches their hands again after the paste.
        foreach (var vk in heldModifiers) inputs.Add(Key(vk, up: false));

        var array = inputs.ToArray();
        var sent = NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != array.Length)
        {
            Log.Write($"SendInput sent {sent}/{array.Length} (error {Marshal.GetLastWin32Error()})");
            return PasteResult.CopiedOnly; // text is on the clipboard at least
        }
        Log.Write($"Pasted '{label}'");
        return PasteResult.Pasted;
    }

    public static PasteResult CopyOnly(Snippet snippet)
    {
        if (!TrySetClipboard(snippet.Text)) return PasteResult.Failed;
        Log.Write($"Copied snippet '{snippet.Label}'");
        return PasteResult.CopiedOnly;
    }

    public static bool TrySetClipboard(string text)
    {
        // Another app (or the Win+V history service) can hold the clipboard
        // open momentarily — retry a few times before giving up.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch when (attempt < 2)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Write($"Clipboard set failed: {ex.Message}");
                return false;
            }
        }
    }

    static bool IsOwnWindow(IntPtr hwnd)
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (new WindowInteropHelper(window).Handle == hwnd) return true;
        }
        return false;
    }

    static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };
}
