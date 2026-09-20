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
    public async Task Invalid_connection_device_key_is_skipped_and_recorded()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"connections\":{\"not-a-device\":{\"connected\":true,\"type\":\"tcp-client\"},\"AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA\":{\"connected\":false}}}");

        var connections = await adapter.GetConnectionsAsync();

        Assert.HasCount(1, connections);
        Assert.HasCount(1, adapter.LastConnectionWarnings);
        StringAssert.Contains(adapter.LastConnectionWarnings[0], "not-a-device");
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
    public async Task Connection_observer_emits_only_actual_kind_changes_in_order()
    {
        var (adapter, handler) = TestAdapter.Create();
        EnqueueConnection(handler, connected: true, paused: false, type: "tcp-client");
        EnqueueConnection(handler, connected: true, paused: false, type: "tcp-server");
        EnqueueConnection(handler, connected: true, paused: false, type: "relay-client");
        EnqueueConnection(handler, connected: true, paused: false, type: "relay-server");
        EnqueueConnection(handler, connected: false, paused: false, type: null);
        EnqueueConnection(handler, connected: false, paused: false, type: null);
        EnqueueConnection(handler, connected: false, paused: true, type: null);
        var startedAt = DateTimeOffset.UtcNow;

        await using var observer = adapter.SubscribeConnectionChangesAsync(TimeSpan.FromMilliseconds(1))
            .GetAsyncEnumerator();
        Assert.IsTrue(await observer.MoveNextAsync());
        var relayed = observer.Current;
        Assert.IsTrue(await observer.MoveNextAsync());
        var offline = observer.Current;
        Assert.IsTrue(await observer.MoveNextAsync());
        var paused = observer.Current;

        Assert.AreEqual(Id, relayed.DeviceId.Value);
        Assert.AreEqual(ConnectionKind.Direct, relayed.OldKind);
        Assert.AreEqual(ConnectionKind.Relay, relayed.NewKind);
        Assert.IsGreaterThanOrEqualTo(startedAt, relayed.Timestamp);
        Assert.AreEqual(ConnectionKind.Relay, offline.OldKind);
        Assert.AreEqual(ConnectionKind.Offline, offline.NewKind);
        Assert.IsGreaterThanOrEqualTo(relayed.Timestamp, offline.Timestamp);
        Assert.AreEqual(ConnectionKind.Offline, paused.OldKind);
        Assert.AreEqual(ConnectionKind.Paused, paused.NewKind);
        Assert.IsGreaterThanOrEqualTo(offline.Timestamp, paused.Timestamp);
        Assert.IsFalse(relayed.IsInitialSnapshot);
        Assert.IsFalse(offline.IsInitialSnapshot);
        Assert.IsFalse(paused.IsInitialSnapshot);
        Assert.HasCount(7, handler.Requests);
    }

    [TestMethod]
    public async Task Connection_observer_initial_snapshot_and_changes_share_one_ordered_stream()
    {
        var (adapter, handler) = TestAdapter.Create();
        EnqueueConnection(handler, connected: true, paused: false, type: "tcp-client");
        EnqueueConnection(handler, connected: false, paused: true, type: null);
        EnqueueConnection(handler, connected: true, paused: false, type: "tcp-server");

        await using var observer = adapter.SubscribeConnectionChangesAsync(
            TimeSpan.FromMilliseconds(1),
            emitInitialSnapshot: true).GetAsyncEnumerator();
        Assert.IsTrue(await observer.MoveNextAsync());
        var snapshot = observer.Current;
        Assert.IsTrue(await observer.MoveNextAsync());
        var paused = observer.Current;
        Assert.IsTrue(await observer.MoveNextAsync());
        var resumed = observer.Current;

        Assert.IsTrue(snapshot.IsInitialSnapshot);
        Assert.AreEqual(ConnectionKind.Unknown, snapshot.OldKind);
        Assert.AreEqual(ConnectionKind.Direct, snapshot.NewKind);
        Assert.IsFalse(paused.IsInitialSnapshot);
        Assert.AreEqual(ConnectionKind.Direct, paused.OldKind);
        Assert.AreEqual(ConnectionKind.Paused, paused.NewKind);
        Assert.IsFalse(resumed.IsInitialSnapshot);
        Assert.AreEqual(ConnectionKind.Paused, resumed.OldKind);
        Assert.AreEqual(ConnectionKind.Direct, resumed.NewKind);
        Assert.HasCount(3, handler.Requests);
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

    private static void EnqueueConnection(
        RecordingHandler handler,
        bool connected,
        bool paused,
        string? type)
    {
        handler.EnqueueJson(new JsonObject
        {
            ["connections"] = new JsonObject
            {
                [Id] = new JsonObject
                {
                    ["connected"] = connected,
                    ["paused"] = paused,
                    ["type"] = type,
                },
            },
        }.ToJsonString());
    }
}
