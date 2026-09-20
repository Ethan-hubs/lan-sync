using System.Text.Json.Nodes;
using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class DevicesFoldersAndIgnoresTests
{
    private const string Id = "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH";

    [TestMethod]
    public async Task Add_folder_serializes_staggered_max_age_as_seconds_string()
    {
        // 保哥 2026-09-20 决策“先按能跑的标准来”：阶段一取 1～2 秒，实现固定取 2 秒。
        Assert.AreEqual(2.0, FolderSpec.DefaultFsWatcherDelaySeconds);
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{}");

        await adapter.AddFolderAsync(new FolderSpec("folder-1", "Folder", @"C:\\sync", [new DeviceId(Id)]));

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.AreEqual(2.0, body["fsWatcherDelayS"]!.GetValue<double>());
        Assert.AreEqual("staggered", body["versioning"]!["type"]!.GetValue<string>());
        var maxAge = body["versioning"]!["params"]!["maxAge"]!;
        Assert.AreEqual("2592000", maxAge.GetValue<string>());
    }

    [TestMethod]
    public async Task Add_folder_allows_fs_watcher_delay_override()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{}");
        var spec = new FolderSpec("folder-1", "Folder", @"C:\sync", [new DeviceId(Id)])
        {
            FsWatcherDelaySeconds = 1.5,
        };

        await adapter.AddFolderAsync(spec);

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!.AsObject();
        Assert.AreEqual(1.5, body["fsWatcherDelayS"]!.GetValue<double>());
    }

    [TestMethod]
    public async Task Ignores_round_trip_through_rest_endpoint()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"ignore\":[\"*.tmp\",\"(?d)cache\"]}");
        handler.EnqueueJson("{}");

        var ignores = await adapter.GetIgnoresAsync("folder with space");
        await adapter.SetIgnoresAsync("folder with space", ["*.tmp", "(?d)cache"]);

        CollectionAssert.AreEqual(new[] { "*.tmp", "(?d)cache" }, ignores.ToArray());
        Assert.AreEqual("/rest/db/ignores?folder=folder%20with%20space", handler.Requests[0].PathAndQuery);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
    }

    [TestMethod]
    public async Task Folder_errors_are_exposed_read_only()
    {
        var (adapter, handler) = TestAdapter.Create();
        handler.EnqueueJson("{\"errors\":[{\"path\":\"bad.txt\",\"error\":\"access denied\"}]}");

        var errors = await adapter.GetFolderErrorsAsync("f1");

        Assert.HasCount(1, errors);
        Assert.AreEqual("bad.txt", errors[0].Path);
        Assert.AreEqual("access denied", errors[0].Message);
        Assert.AreEqual(HttpMethod.Get, handler.Requests.Single().Method);
    }
}
