using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class C1C6ContractTests
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
            await pair.A.PauseAsync();
            await WaitUntilAsync(async () => !(await pair.A.GetConnectionAsync(pair.IdB)).Connected, TimeSpan.FromSeconds(30));
        }
        finally
        {
            await pair.A.ResumeAsync();
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
