using System.Text.Json;
using Palon.Agent;
using Xunit;

namespace Palon.Tests;

public class ToolRegistryTests
{
    [Fact]
    public void Tool_names_are_unique()
    {
        var names = ToolRegistry.CreateDefault().Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Every_parameters_schema_is_a_valid_json_object()
    {
        foreach (var tool in ToolRegistry.CreateDefault())
        {
            using var doc = JsonDocument.Parse(tool.ParametersJson);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void Tools_spec_serializes_to_openai_function_shape()
    {
        var spec = ToolRegistry.ToToolsSpec(ToolRegistry.CreateDefault());
        var json = JsonSerializer.Serialize(spec);
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            Assert.Equal("function", entry.GetProperty("type").GetString());
            var function = entry.GetProperty("function");
            Assert.False(string.IsNullOrEmpty(function.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrEmpty(function.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, function.GetProperty("parameters").ValueKind);
        }
    }
}
