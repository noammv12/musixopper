using System.Text.Json;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class AiRequestBodyTests
{
    static AiChat.Provider DeepSeek(string model) => new("DeepSeek", "https://api.deepseek.com/chat/completions", model, "k", IsGemini: false);
    static AiChat.Provider Gemini(string model) => new("Gemini", "https://g/chat/completions", model, "k", IsGemini: true);

    static JsonElement Wire(Dictionary<string, object> body) => JsonDocument.Parse(AiChat.Serialize(body)).RootElement;

    static readonly object[] Messages = [new { role = "user", content = "hi" }];

    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-chat")]
    public void DeepSeek_DisablesThinking_AndKeepsCap(string model)
    {
        var json = Wire(AiChat.BuildBody(DeepSeek(model), Messages, 0.3, 1200, tools: null));
        Assert.Equal("disabled", json.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(json.TryGetProperty("reasoning_effort", out _));
        Assert.Equal(1200, json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void DeepSeekReasoner_GetsNoThinkingToggle()
    {
        var json = Wire(AiChat.BuildBody(DeepSeek("deepseek-reasoner"), Messages, 0.3, 500, tools: null));
        Assert.False(json.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("gemini-3.8-flash")]
    [InlineData("gemini-3.5-flash")]
    public void Gemini_LowEffort_NoThinkingConfigPair(string model)
    {
        var json = Wire(AiChat.BuildBody(Gemini(model), Messages, 0.3, 300, tools: null));
        Assert.Equal("low", json.GetProperty("reasoning_effort").GetString());
        Assert.False(json.TryGetProperty("thinking", out _));
        Assert.False(json.TryGetProperty("extra_body", out _)); // never combined with reasoning_effort
        Assert.True(json.GetProperty("max_tokens").GetInt32() >= 4096);
    }

    [Fact]
    public void KeyTest_CapIsSane()
    {
        Assert.True(AiChat.KeyTestMaxTokens >= 64);
        var json = Wire(AiChat.BuildBody(DeepSeek("deepseek-flash"), Messages, 0, AiChat.KeyTestMaxTokens, tools: null));
        Assert.Equal(64, json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Boost_RaisesCapOnce_AndMarkerNeverReachesWire()
    {
        var p = DeepSeek("deepseek-flash");
        var body = AiChat.BuildBody(p, Messages, 0.3, 300, tools: null);
        var boosted = AiChat.BoostForEmptyLength(p, body);
        Assert.NotNull(boosted);
        var json = Wire(boosted!);
        Assert.Equal(1200, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("disabled", json.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(json.TryGetProperty(AiChat.BoostedMarker, out _));
        Assert.Null(AiChat.BoostForEmptyLength(p, boosted!)); // only one retry
        Assert.Equal(300, (int)body["max_tokens"]); // original untouched
    }

    [Fact]
    public void Shape_EmptyAtLength_IgnoresReasoningContent()
    {
        using var doc = JsonDocument.Parse("""
            {"choices":[{"message":{"role":"assistant","content":"","reasoning_content":"Let me think about ping"},
              "finish_reason":"length"}],"usage":{"completion_tokens":8}}
            """);
        var shape = AiChat.ReadShape(doc.RootElement);
        Assert.True(shape.EmptyAtLength);
        Assert.Equal("", shape.Content);
        Assert.Equal(23, shape.ReasoningChars);
        Assert.Equal("8", shape.CompletionTokens);
    }

    [Fact]
    public void Shape_NullContentAtStop_IsNotLengthCase()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"message":{"content":null},"finish_reason":"stop"}]}""");
        var shape = AiChat.ReadShape(doc.RootElement);
        Assert.False(shape.EmptyAtLength);
        Assert.Null(shape.Content);
        Assert.Equal("?", shape.CompletionTokens);
    }

    [Fact]
    public void Shape_ShortContent_IsAnAnswer()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"message":{"content":"ok"},"finish_reason":"length"}]}""");
        Assert.False(AiChat.ReadShape(doc.RootElement).EmptyAtLength);
    }

    [Theory]
    [InlineData("DeepSeek: empty reply (finish=length, completion_tokens=8)", "המודל החזיר תשובה ריקה")]
    [InlineData("DeepSeek: key rejected (401)", "המפתח נדחה")]
    [InlineData(null, "שגיאת AI לא ידועה")]
    public void FailureReason_IsHebrew(string? error, string expected)
    {
        Assert.StartsWith(expected, AiChat.DescribeFailureHe(error));
    }
}
