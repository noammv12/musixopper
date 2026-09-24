using Palon.Notes;
using Palon.Terminal;
using Xunit;

namespace Palon.Tests;

public class AiModelsTests
{
    const string G = "gemini-3.8-flash";
    const string D = "deepseek-flash";

    static string Order(string? choice, bool g, bool d) =>
        string.Join(",", AiModels.Resolve(choice, g, d, G, D).Select(r => $"{r.Provider}:{r.Model}"));

    [Fact]
    public void Catalog_has_auto_first_and_unique_ids()
    {
        Assert.Equal(AiModels.AutoId, AiModels.Catalog[0].Id);
        Assert.Equal(AiModels.Catalog.Count, AiModels.Catalog.Select(o => o.Id).Distinct().Count());
        Assert.Contains(AiModels.Catalog, o => o.Id == Settings.DefaultGeminiModel);
        Assert.Contains(AiModels.Catalog, o => o.Id == Settings.DefaultDeepSeekModel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gpt-9")]
    public void Unknown_choice_is_auto(string? id) => Assert.Equal(AiModels.AutoId, AiModels.Find(id).Id);

    [Fact]
    public void Auto_keeps_gemini_primary() =>
        Assert.Equal($"Gemini:{G},DeepSeek:{D}", Order("auto", true, true));

    [Fact]
    public void Picked_gemini_model_is_primary_with_deepseek_fallback() =>
        Assert.Equal($"Gemini:gemini-3.5-flash,DeepSeek:{D}", Order("gemini-3.5-flash", true, true));

    [Fact]
    public void Picked_deepseek_model_is_primary_with_gemini_fallback() =>
        Assert.Equal($"DeepSeek:deepseek-v4-pro,Gemini:{G}", Order("deepseek-v4-pro", true, true));

    [Fact]
    public void Missing_key_drops_that_provider() =>
        Assert.Equal($"Gemini:{G}", Order("deepseek-v4-pro", true, false));

    [Fact]
    public void No_keys_no_chain() => Assert.Empty(AiModels.Resolve("auto", false, false, G, D));

    [Fact]
    public void Availability_follows_keys()
    {
        var pro = AiModels.Find("deepseek-v4-pro");
        Assert.False(AiModels.IsAvailable(pro, hasGemini: true, hasDeepSeek: false));
        Assert.True(AiModels.IsAvailable(pro, hasGemini: false, hasDeepSeek: true));
        Assert.True(AiModels.IsAvailable(AiModels.Find("auto"), false, true));
        Assert.False(AiModels.IsAvailable(AiModels.Find("auto"), false, false));
    }

    [Fact]
    public void Greeting_carries_the_name()
    {
        var noon = new DateTime(2026, 9, 24, 13, 0, 0);
        Assert.Equal("צהריים טובים, דני", He.Greeting(noon, " דני "));
        Assert.Equal("צהריים טובים", He.Greeting(noon, null));
    }
}
