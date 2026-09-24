using Palon.Agent;
using Xunit;

namespace Palon.Tests;

public class ModelMigrationTests
{
    [Theory]
    [InlineData("gemini-flash-latest")]
    [InlineData("gemini-3.7-flash")]
    [InlineData(" Gemini-3.7-Flash ")]
    public void Old_gemini_defaults_are_retired(string stored) =>
        Assert.True(Settings.IsRetiredGeminiDefault(stored));

    [Theory]
    [InlineData(null)]
    [InlineData(Settings.DefaultGeminiModel)]
    [InlineData("gemini-3.8-pro")]
    [InlineData("gemini-2.5-flash")]
    public void Current_and_custom_gemini_ids_stay(string? stored) =>
        Assert.False(Settings.IsRetiredGeminiDefault(stored));

    [Theory]
    [InlineData("deepseek-chat")]
    [InlineData("deepseek-reasoner")]
    public void Retired_deepseek_ids_are_cleared(string stored) =>
        Assert.True(Settings.IsRetiredDeepSeekModel(stored));

    [Theory]
    [InlineData(null)]
    [InlineData(Settings.DefaultDeepSeekModel)]
    [InlineData("deepseek-v4-pro")]
    public void Current_deepseek_ids_stay(string? stored) =>
        Assert.False(Settings.IsRetiredDeepSeekModel(stored));

    [Fact]
    public void Persona_frames_every_voiced_prompt()
    {
        Assert.StartsWith(PalonPersona.Core, PalonPersona.Speaking("task"));
        Assert.StartsWith(PalonPersona.Core, PalonPersona.Ghostwriting("task"));
        Assert.EndsWith("task", PalonPersona.Ghostwriting("task"));
    }
}
