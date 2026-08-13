namespace Bridget;

/// <summary>
/// Named events that let short-lived CLI invocations (wired to softphone
/// call-event handlers, or a second launch of the exe) talk to the running
/// tray instance.
/// </summary>
static class TraySignals
{
    public const string CallStartName = @"Local\Bridget.CallStart";
    public const string CallEndName = @"Local\Bridget.CallEnd";
    public const string ShowFlyoutName = @"Local\Bridget.ShowFlyout";
}
