using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class ConnectionsEventsPauseTests
{
    private const string Id = "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH";

    [TestMethod]
    [DataRow(true, "tcp-client", ConnectionKind.Direct)]
    [DataRow(true, "tcp-server", ConnectionKind.Direct)]
    [DataRow(true, "quic-client", ConnectionKind.Direct)]
    [DataRow(true, "quic-server", ConnectionKind.Direct)]
    [DataRow(true, "relay-client", ConnectionKind.Relay)]
    [DataRow(true, "relay-server", ConnectionKind.Relay)]
    [DataRow(true, "tcp-future", ConnectionKind.Direct)]
    [DataRow(true, "quic-future", ConnectionKind.Direct)]
    [DataRow(true, "relay-future", ConnectionKind.Relay)]
    [DataRow(true, "unknown-transport", ConnectionKind.Unknown)]
    [DataRow(false, "tcp-client", ConnectionKind.Offline)]
    public async Task Connection_type_mapping_follows_contract(bool connected, string type, ConnectionKind expected)
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson(new JsonObject
        {
            ["connections"] = new JsonObject
            {
                [Id] = new JsonObject
                {
                    ["connected"] = connected,
                    ["paused"] = true,
                    ["type"] = type,
                },
            },
        }.ToJsonString());

        var connection = await adapter.GetConnectionAsync(new DeviceId(Id));

        Assert.AreEqual(expected, connection.Kind);
        Assert.IsTrue(connection.Paused);
    }

    [TestMethod]
    public async Task Events_include_since_and_parse_payload()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("[{\"id\":42,\"time\":\"2026-01-02T03:04:05Z\",\"type\":\"LocalChangeDetected\",\"data\":{\"folder\":\"f1\"}}]");

        var events = await adapter.GetEventsAsync(41, TimeSpan.FromSeconds(12), 7, ["LocalChangeDetected", "StateChanged"]);

        Assert.HasCount(1, events);
        Assert.AreEqual(42L, events[0].Id);
        Assert.AreEqual("LocalChangeDetected", events[0].Type);
        Assert.AreEqual("/rest/events?since=41&timeout=12&limit=7&events=LocalChangeDetected,StateChanged", handler.Requests.Single().PathAndQuery);
    }

    [TestMethod]
    public async Task Empty_event_timeout_is_a_normal_result()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("[]");

        var events = await adapter.GetEventsAsync(timeout: TimeSpan.Zero);

        Assert.IsEmpty(events);
    }

    [TestMethod]
    public async Task Pause_and_resume_support_global_and_device_scope()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        var device = new DeviceId(Id);

        await adapter.PauseAsync();
        await adapter.ResumeAsync();
        await adapter.PauseAsync(device);
        await adapter.ResumeAsync(device);

        CollectionAssert.AreEqual(
            new[]
            {
                "/rest/system/pause",
                "/rest/system/resume",
                $"/rest/system/pause?device={Id}",
                $"/rest/system/resume?device={Id}",
            },
            handler.Requests.Select(request => request.PathAndQuery).ToArray());
    }
}
