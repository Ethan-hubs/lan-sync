using LanSync.Core;
using LanSync.Syncthing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class C1C7ContractTests
{
    private static SyncthingPair? _pair;
    private static string? _skipReason;

    [ClassInitialize]
    public static async Task Initialize(TestContext _)
    {
        (_pair, _skipReason) = await SyncthingPair.CreateAsync();
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (_pair is not null)
        {
            await _pair.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task B1_temporary_files_are_listed_read_only()
    {
        var pair = RequirePair();
        var relativePath = $"diagnostics/~syncthing~{Guid.NewGuid():N}.tmp";
        var fullPath = Path.Combine(pair.FolderA, relativePath.Replace('/', Path.DirectorySeparatorChar));
        LongPath.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        LongPath.WriteAllText(fullPath, "residual");

        try
        {
            var files = await pair.A.GetTemporaryFilesAsync(pair.FolderId);

            CollectionAssert.Contains(files.Select(file => file.RelativePath).ToArray(), relativePath);
            Assert.IsTrue(LongPath.Exists(fullPath), "Read-only diagnostics must not delete temporary files.");
        }
        finally
        {
            LongPath.DeleteFile(fullPath);
        }
    }

    [TestMethod]
    public async Task C1_REST_readable_configuration()
    {
        var pair = RequirePair();
        var config = await pair.A.GetConfigurationAsync();

        Assert.IsNotNull(config["devices"]);
        Assert.IsNotNull(config["folders"]);
        Assert.IsTrue((await pair.A.GetFoldersAsync()).OfType<System.Text.Json.Nodes.JsonObject>()
            .Any(folder => folder["id"]?.GetValue<string>() == pair.FolderId));
    }

    [TestMethod]
    public async Task C2_connection_type_mapping()
    {
        var pair = RequirePair();
        var aToB = await pair.A.WaitForConnectionAsync(pair.IdB, TimeSpan.FromSeconds(60));
        var bToA = await pair.B.WaitForConnectionAsync(pair.IdA, TimeSpan.FromSeconds(60));

        Assert.IsTrue(aToB.Kind is ConnectionKind.Direct or ConnectionKind.Relay, $"A->B type={aToB.Type}");
        Assert.IsTrue(bToA.Kind is ConnectionKind.Direct or ConnectionKind.Relay, $"B->A type={bToA.Type}");
    }

    [TestMethod]
    public async Task C3_two_instance_file_sync()
    {
        var pair = RequirePair();
        var fileName = $"c3-{Guid.NewGuid():N}.txt";
        var source = Path.Combine(pair.FolderA, fileName);
        var target = Path.Combine(pair.FolderB, fileName);
        var payload = $"LanSync C3 {Guid.NewGuid():N}";

        LongPath.WriteAllText(source, payload);
        await pair.A.RescanAsync(pair.FolderId, fileName);
        await pair.WaitForFileAsync(target, payload, TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public async Task C4_events_subscription()
    {
        var pair = RequirePair();
        var since = 0L;
        while (true)
        {
            var buffered = await pair.A.GetEventsAsync(since, TimeSpan.Zero, 1000, ["LocalChangeDetected"]);
            if (buffered.Count == 0)
            {
                break;
            }

            var next = buffered.Max(item => item.Id);
            if (next <= since)
            {
                break;
            }

            since = next;
        }

        var fileName = $"c4-{Guid.NewGuid():N}.txt";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        var found = false;
        var observedTypes = new HashSet<string>(StringComparer.Ordinal);
        var firstPoll = pair.A.GetEventsAsync(since, TimeSpan.FromSeconds(10), 100, ["LocalChangeDetected"]);
        await Task.Delay(500);
        LongPath.WriteAllText(Path.Combine(pair.FolderA, fileName), "event probe");

        while (DateTimeOffset.UtcNow < deadline && !found)
        {
            var events = await firstPoll;
            if (events.Count > 0)
            {
                since = events.Max(item => item.Id);
                foreach (var item in events)
                {
                    observedTypes.Add(item.Type);
                }

                found = events.Any(item => item.Type == "LocalChangeDetected");
            }

            firstPoll = pair.A.GetEventsAsync(since, TimeSpan.FromSeconds(5), 100, ["LocalChangeDetected"]);
        }

        Assert.IsTrue(found, $"LocalChangeDetected was not observed through /rest/events. Seen: {string.Join(", ", observedTypes)}");
    }

    [TestMethod]
    public async Task C5_pause_resume()
    {
        var pair = RequirePair();
        try
        {
            await pair.A.PauseAsync(pair.IdB);
            await WaitUntilAsync(async () => !(await pair.A.GetConnectionAsync(pair.IdB)).Connected, TimeSpan.FromSeconds(30));
        }
        finally
        {
            await pair.A.ResumeAsync(pair.IdB);
        }

        var resumed = await pair.A.WaitForConnectionAsync(pair.IdB, TimeSpan.FromSeconds(60));
        Assert.IsTrue(resumed.Connected);
    }

    [TestMethod]
    public async Task C6_ignores_effective_through_REST()
    {
        var pair = RequirePair();
        var original = await pair.A.GetIgnoresAsync(pair.FolderId);
        var extension = ".lansw-ignore-" + Guid.NewGuid().ToString("N");
        var pattern = "*" + extension;
        var fileName = "probe" + extension;
        var source = Path.Combine(pair.FolderA, fileName);
        var target = Path.Combine(pair.FolderB, fileName);

        try
        {
            await pair.A.SetIgnoresAsync(pair.FolderId, original.Concat([pattern]).ToArray());
            var readBack = await pair.A.GetIgnoresAsync(pair.FolderId);
            CollectionAssert.Contains(readBack.ToArray(), pattern);
            LongPath.WriteAllText(source, "must stay local");
            await pair.A.RescanAsync(pair.FolderId, fileName);
            await Task.Delay(TimeSpan.FromSeconds(10));
            Assert.IsFalse(LongPath.Exists(target), "Ignored file unexpectedly synchronized.");
        }
        finally
        {
            await pair.A.SetIgnoresAsync(pair.FolderId, original);
            LongPath.DeleteFile(source);
        }
    }

    [TestMethod]
    public async Task C7_version_restore_transaction()
    {
        var pair = RequirePair();
        var fileName = $"c7-{Guid.NewGuid():N}.txt";
        var source = Path.Combine(pair.FolderA, fileName);
        var target = Path.Combine(pair.FolderB, fileName);

        LongPath.WriteAllText(source, "v1");
        await pair.A.RescanAsync(pair.FolderId, fileName);
        await pair.WaitForFileAsync(target, "v1", TimeSpan.FromSeconds(60));
        await Task.Delay(TimeSpan.FromSeconds(2));

        LongPath.WriteAllText(source, "v2");
        await pair.A.RescanAsync(pair.FolderId, fileName);
        await pair.WaitForFileAsync(target, "v2", TimeSpan.FromSeconds(60));

        IReadOnlyList<VersionEntry>? archived = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var versions = await pair.B.GetVersionsAsync(pair.FolderId, fileName);
            if (versions.TryGetValue(fileName, out archived) && archived.Count > 0)
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.IsNotNull(archived, "The receiving instance did not archive v1.");
        Assert.IsNotEmpty(archived);
        var restore = await pair.B.RestoreVersionAsync(
            pair.FolderId,
            fileName,
            archived[0].VersionTime,
            new Dictionary<DeviceId, SyncthingAdapter> { [pair.IdA] = pair.A });

        CollectionAssert.Contains(restore.PausedDevices.Select(device => device.Value).ToArray(), pair.IdA.Value);
        await pair.WaitForFileAsync(target, "v1", TimeSpan.FromSeconds(30));
        await pair.WaitForFileAsync(source, "v1", TimeSpan.FromSeconds(60));
        Assert.IsTrue((await pair.B.WaitForConnectionAsync(pair.IdA, TimeSpan.FromSeconds(60))).Connected);
    }

    private static SyncthingPair RequirePair()
    {
        if (_pair is null)
        {
            Assert.Inconclusive(_skipReason ?? "Syncthing pair is unavailable.");
        }

        return _pair;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Condition was not met within {timeout}.");
    }
}
