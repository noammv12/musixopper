using System.Windows.Forms;

namespace Musixopper;

/// <summary>
/// The tray icon: left-click opens the flyout, right-click gives a
/// two-item fallback menu. Everything else lives in the flyout.
/// </summary>
sealed class TrayHost : IDisposable
{
    readonly NotifyIcon _icon;
    CallState _state = CallState.Idle;

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public TrayHost()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open Musixopper");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };

        Theme.Changed += Refresh;
        Refresh();
    }

    public void SetState(CallState state)
    {
        if (_state == state) return;
        _state = state;
        Refresh();
    }

    void Refresh()
    {
        _icon.Icon = TrayIconRenderer.Get(_state, Theme.SystemLight);
        _icon.Text = _state switch
        {
            CallState.OnCall => "Musixopper — on a call, music paused",
            CallState.Disabled => "Musixopper — off",
            _ => "Musixopper — listening for calls",
        };
    }

    public void Dispose()
    {
        Theme.Changed -= Refresh;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
