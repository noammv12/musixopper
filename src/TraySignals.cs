namespace Musixopper;

/// <summary>
/// Named events that let the short-lived CLI invocations (wired to
/// softphone call-event handlers) hand call state to a running tray
/// instance instead of acting on the media sessions themselves.
/// </summary>
static class TraySignals
{
    public const string CallStartName = @"Local\Musixopper.CallStart";
    public const string CallEndName = @"Local\Musixopper.CallEnd";
}
