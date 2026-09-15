using System.Net;
using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class VersionsTests
{
    private const string Local = "AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA-AAAAAAA";
    private const string Peer1 = "BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB-BBBBBBB";
    private const string Peer2 = "CCCCCCC-CCCCCCC-CCCCCCC-CCCCCCC-CCCCCCC-CCCCCCC-CCCCCCC-CCCCCCC";
    private const string AlreadyPaused = "DDDDDDD-DDDDDDD-DDDDDDD-DDDDDDD-DDDDDDD-DDDDDDD-DDDDDDD-DDDDDDD";

    [TestMethod]
    public async Task Get_versions_maps_response_dictionary()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"docs/file.txt\":[{\"versionTime\":\"20260914-010203\",\"modTime\":\"2026-09-14T01:02:03Z\",\"size\":12}]}");

        var versions = await adapter.GetVersionsAsync("folder 1", "docs/file.txt");

        Assert.IsTrue(versions.TryGetValue("docs/file.txt", out var entries));
        Assert.HasCount(1, entries);
        Assert.AreEqual("20260914-010203", entries[0].VersionTime);
        Assert.AreEqual(12L, entries[0].Size);
        Assert.AreEqual("2026-09-14T01:02:03Z", entries[0].ModTime);
        Assert.AreEqual("/rest/folder/versions?folder=folder%201&file=docs%2Ffile.txt", handler.Requests.Single().PathAndQuery);
    }

    [TestMethod]
    public async Task Restore_pauses_only_shared_unpaused_peers_and_resumes_only_them()
    {
        var (adapter, handler) = TestAdapter.Create();
        var (peer1Adapter, peer1Handler) = TestAdapter.Create();
        var (peer2Adapter, peer2Handler) = TestAdapter.Create();
        var (pausedAdapter, pausedHandler) = TestAdapter.Create();
        EnqueueScope(handler, includeSecondPeer: true);
        EnqueueSelectedVersion(handler);
        EnqueueIdleFolder(handler);
        handler.EnqueueJson("{\"connections\":{}}");
        handler.EnqueueJson("{\"connections\":{}}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{}");
        handler.EnqueueJson("{\"local\":{\"modified\":\"2026-09-14T01:02:03.436Z\"}}");
        EnqueuePeer(peer1Handler, paused: false, includePauseResume: true);
        EnqueuePeer(peer2Handler, paused: false, includePauseResume: true);
        EnqueuePeer(pausedHandler, paused: true, includePauseResume: false);

        var result = await adapter.RestoreVersionAsync(
            "folder",
            "file.txt",
            "20260914-010203",
            PeerAdapters(peer1Adapter, peer2Adapter, pausedAdapter));

        CollectionAssert.AreEqual(new[] { Peer1, Peer2 }, result.PausedDevices.Select(device => device.Value).ToArray());
        Assert.AreEqual("folder", result.FolderId);
        Assert.AreEqual("file.txt", result.RelativePath);
        Assert.AreEqual("20260914-010203", result.VersionTime);
        CollectionAssert.AreEqual(new[] { Peer2, Peer1 }, result.ResumedDevices.Select(device => device.Value).ToArray());
        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[]
            {
                "/rest/config/folders/folder",
                "/rest/system/status",
                "/rest/folder/versions?folder=folder&file=file.txt",
                "/rest/db/status?folder=folder",
                "/rest/system/connections",
                "/rest/system/connections",
                "/rest/folder/versions?folder=folder",
                "/rest/folder/versions?folder=folder&file=file.txt",
                "/rest/db/file?folder=folder&file=file.txt",
            },
            handler.Requests.Select(request => request.PathAndQuery).ToArray());
        StringAssert.Contains(handler.Requests[6].Body, "file.txt");
        AssertPeerTransaction(peer1Handler, pauseAndResume: true);
        AssertPeerTransaction(peer2Handler, pauseAndResume: true);
        AssertPeerTransaction(pausedHandler, pauseAndResume: false);
    }

    [TestMethod]
    public async Task Restore_nonempty_error_dictionary_fails_and_still_resumes()
    {
        var (adapter, handler) = TestAdapter.Create();
        var (peerAdapter, peerHandler) = TestAdapter.Create();
        var (pausedAdapter, pausedHandler) = TestAdapter.Create();
        EnqueueScope(handler, includeSecondPeer: false);
        EnqueueSelectedVersion(handler);
        EnqueueIdleFolder(handler);
        handler.EnqueueJson("{\"connections\":{}}");
        handler.EnqueueJson("{\"file.txt\":\"restore failed\"}");
        EnqueuePeer(peerHandler, paused: false, includePauseResume: true);
        EnqueuePeer(pausedHandler, paused: true, includePauseResume: false);

        var exception = await Assert.ThrowsExactlyAsync<VersionRestoreException>(
            () => adapter.RestoreVersionAsync(
                "folder",
                "file.txt",
                "20260914-010203",
                PeerAdapters(peerAdapter, null, pausedAdapter)));

        Assert.AreEqual("restore failed", exception.Errors["file.txt"]?.GetValue<string>());
        Assert.AreEqual($"/rest/system/resume?device={Local}", peerHandler.Requests[^1].PathAndQuery);
    }

    [TestMethod]
    public async Task Pause_failure_resumes_only_peers_that_were_successfully_paused()
    {
        var (adapter, handler) = TestAdapter.Create();
        var (peer1Adapter, peer1Handler) = TestAdapter.Create();
        var (peer2Adapter, peer2Handler) = TestAdapter.Create();
        var (pausedAdapter, pausedHandler) = TestAdapter.Create();
        EnqueueScope(handler, includeSecondPeer: true);
        EnqueueSelectedVersion(handler);
        EnqueueIdleFolder(handler);
        handler.EnqueueJson("{\"connections\":{}}");
        EnqueuePeer(peer1Handler, paused: false, includePauseResume: true);
        peer2Handler.EnqueueJson($"{{\"deviceID\":\"{Local}\",\"paused\":false}}");
        peer2Handler.EnqueueJson("{\"error\":\"pause failed\"}", HttpStatusCode.InternalServerError);
        EnqueuePeer(pausedHandler, paused: true, includePauseResume: false);

        _ = await Assert.ThrowsExactlyAsync<SyncthingApiException>(
            () => adapter.RestoreVersionAsync(
                "folder",
                "file.txt",
                "20260914-010203",
                PeerAdapters(peer1Adapter, peer2Adapter, pausedAdapter)));

        Assert.AreEqual($"/rest/system/resume?device={Local}", peer1Handler.Requests[^1].PathAndQuery);
        Assert.IsFalse(peer2Handler.Requests.Any(request => request.PathAndQuery.StartsWith("/rest/system/resume", StringComparison.Ordinal)));
        Assert.IsFalse(handler.Requests.Any(request =>
            request.Method == HttpMethod.Post &&
            request.PathAndQuery.StartsWith("/rest/folder/versions", StringComparison.Ordinal)));
    }

    private static Dictionary<DeviceId, SyncthingAdapter> PeerAdapters(
        SyncthingAdapter peer1,
        SyncthingAdapter? peer2,
        SyncthingAdapter alreadyPaused)
    {
        var peers = new Dictionary<DeviceId, SyncthingAdapter>
        {
            [new DeviceId(Peer1)] = peer1,
            [new DeviceId(AlreadyPaused)] = alreadyPaused,
        };
        if (peer2 is not null)
        {
            peers[new DeviceId(Peer2)] = peer2;
        }

        return peers;
    }

    private static void AssertPeerTransaction(RecordingHandler handler, bool pauseAndResume)
    {
        var expected = new List<string> { $"/rest/config/devices/{Local}" };
        if (pauseAndResume)
        {
            expected.Add($"/rest/system/pause?device={Local}");
            expected.Add("/rest/system/connections");
            expected.Add($"/rest/system/resume?device={Local}");
        }

        CollectionAssert.AreEqual(expected, handler.Requests.Select(request => request.PathAndQuery).ToArray());
    }

    private static void EnqueueScope(RecordingHandler handler, bool includeSecondPeer)
    {
        var secondFolderDevice = includeSecondPeer ? $",{{\"deviceID\":\"{Peer2}\"}}" : string.Empty;
        handler.EnqueueJson(
            $"{{\"id\":\"folder\",\"devices\":[{{\"deviceID\":\"{Local}\"}},{{\"deviceID\":\"{Peer1}\"}}{secondFolderDevice},{{\"deviceID\":\"{AlreadyPaused}\"}}]}}");
        handler.EnqueueJson($"{{\"myID\":\"{Local}\"}}");
    }

    private static void EnqueuePeer(RecordingHandler handler, bool paused, bool includePauseResume)
    {
        handler.EnqueueJson($"{{\"deviceID\":\"{Local}\",\"paused\":{paused.ToString().ToLowerInvariant()}}}");
        if (includePauseResume)
        {
            handler.EnqueueJson("{}");
            handler.EnqueueJson("{\"connections\":{}}");
            handler.EnqueueJson("{}");
        }
    }

    private static void EnqueueSelectedVersion(RecordingHandler handler) =>
        handler.EnqueueJson(
            "{\"file.txt\":[{\"versionTime\":\"20260914-010203\",\"modTime\":\"2026-09-14T01:02:03Z\",\"size\":12}]}");

    private static void EnqueueIdleFolder(RecordingHandler handler) =>
        handler.EnqueueJson("{\"state\":\"idle\"}");
}
