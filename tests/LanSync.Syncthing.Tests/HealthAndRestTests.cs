using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class HealthAndRestTests
{
    [TestMethod]
    public async Task Health_reads_version_status_and_restart_state()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"version\":\"v2.1.5\"}");
        handler.EnqueueJson("{\"myID\":\"ABC\",\"uptime\":12,\"goroutines\":4}");
        handler.EnqueueJson("{\"requiresRestart\":true}");

        var health = await adapter.GetHealthAsync();

        Assert.AreEqual("v2.1.5", health.Version);
        Assert.AreEqual("ABC", health.DeviceId);
        Assert.AreEqual(12L, health.UptimeSeconds);
        Assert.AreEqual(4, health.Goroutines);
        Assert.IsTrue(health.RestartRequired);
        CollectionAssert.AreEqual(
            new[] { "/rest/system/version", "/rest/system/status", "/rest/config/restart-required" },
            handler.Requests.Select(request => request.PathAndQuery).ToArray());
        Assert.IsTrue(handler.Requests.All(request => request.ApiKey == "test-key"));
    }

    [TestMethod]
    public async Task Rest_client_throws_status_and_body_for_failed_request()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"error\":\"denied\"}", HttpStatusCode.Forbidden);

        var exception = await Assert.ThrowsAsync<SyncthingApiException>(() => adapter.PingAsync());

        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        StringAssert.Contains(exception.ResponseBody, "denied");
    }
}
