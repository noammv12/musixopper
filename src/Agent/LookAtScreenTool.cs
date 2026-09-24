using System.Text.Json;
using Palon.Vision;

namespace Palon.Agent;

/// <summary>
/// Reads what's on the user's screen — only on their request. The user
/// frames a region (or clicks a window), sees the exact image in the
/// privacy gate and presses Send; cancelling at any step is a normal
/// outcome. purpose=receipt extracts the payment fields and opens the New
/// deal sheet pre-filled for the user to check and save (never saved here).
/// </summary>
sealed class LookAtScreenTool : AgentTool
{
    public override string Name => "look_at_screen";
    public override string Description =>
        "Read what is on the user's screen. Use ONLY when the user asks about their screen: " +
        "'מה כתוב פה?', 'סכם את המסך', 'what does this say', 'קח את הפרטים מהקבלה'. The user then selects " +
        "a region (or a window) and approves sending it; you get the verbatim text and a reading. " +
        "Set purpose to \"receipt\" when they want a deposit/payment receipt turned into a deal — " +
        "Palon then opens the New deal form pre-filled for them to check. Never call it on your own initiative.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "mode":{"type":"string","enum":["region","window"],"description":"region (default): the user drags a rectangle; window: the user clicks a window. Use window only if they said 'window'/'חלון'."},
          "purpose":{"type":"string","enum":["read","receipt"],"description":"read (default) answers the request; receipt fills a new deal from a payment receipt."},
          "request":{"type":"string","description":"What the user wants from the screen, in their words (e.g. 'סכם את המייל')."}
        }}
        """;

    // The user is selecting and approving — give them time.
    public override TimeSpan Timeout => TimeSpan.FromMinutes(3);

    public override async Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var windowMode = Str(args, "mode") == "window";
        if (Str(args, "purpose") == "receipt")
        {
            var (receipt, rError) = await ScreenReader.ReceiptAsync(windowMode, ct);
            if (receipt is null) return Cancelled(rError);
            ScreenReader.OpenDealSheet(receipt);
            var summary = ScreenReader.ReceiptSummary(receipt);
            return new ToolOutcome(summary, EndTurn: true, Toast: summary);
        }
        var (read, error) = await ScreenReader.ReadAsync(Str(args, "request"), windowMode, ct);
        if (read is null) return Cancelled(error);
        return new ToolOutcome(
            "Verbatim text from the image:\n" + read.Transcript + "\n\nReading of the image for the user's request:\n" + read.Answer +
            "\n\nAnswer the user from this.");
    }

    static ToolOutcome Cancelled(string? error) => error is null
        ? new ToolOutcome("The user cancelled the screen capture.", EndTurn: true, Toast: "בוטל — לא נשלח כלום.")
        : new ToolOutcome(error, EndTurn: true, Toast: error);
}
