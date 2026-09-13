using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public async Task Update_options_preserves_unknown_fields_and_puts_only_when_changed()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"relaysEnabled\":true,\"futureOption\":{\"nested\":7}}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{\"requiresRestart\":false}");

        var result = await adapter.UpdateOptionsAsync(new Dictionary<string, JsonNode?>
        {
            ["relaysEnabled"] = JsonValue.Create(false),
        });

        Assert.HasCount(1, result.ChangedProperties);
        var put = handler.Requests.Single(request => request.Method == HttpMethod.Put);
        var body = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.AreEqual(7, body["futureOption"]!["nested"]!.GetValue<int>());
        Assert.IsFalse(body["relaysEnabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task Update_options_does_not_put_when_unchanged()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"relaysEnabled\":true,\"unknown\":1}");
        handler.EnqueueJson("{\"requiresRestart\":false}");

        var result = await adapter.UpdateOptionsAsync(new Dictionary<string, JsonNode?>
        {
            ["relaysEnabled"] = JsonValue.Create(true),
        });

        Assert.IsEmpty(result.ChangedProperties);
        Assert.IsFalse(handler.Requests.Any(request => request.Method == HttpMethod.Put));
    }

    [TestMethod]
    public async Task Update_configuration_preserves_unknown_fields_and_skips_unchanged_put()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"devices\":[],\"folders\":[],\"futureRoot\":9}");
        handler.EnqueueJson("{\"requiresRestart\":false}");

        var result = await adapter.UpdateConfigurationAsync(new Dictionary<string, JsonNode?>
        {
            ["futureRoot"] = JsonValue.Create(9),
        });

        Assert.IsEmpty(result.ChangedProperties);
        Assert.IsFalse(handler.Requests.Any(request => request.Method == HttpMethod.Put));
    }
}
