using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LanSync.Core;
using LanSync.Syncthing;

namespace LanSync.IntegrationTests;

internal sealed class SyncthingPair : IAsyncDisposable
{
    private const string RequiredVersion = "v2.1.5";
    private const string RequiredWindowsSha256 = "36a0f7bc372f64fa7cc4f5654fa324c0dd9f7fef2e07565e00c6e1cf73f50344";
    private const string RequiredLinuxSha256 = "ab0ea5f307101e5aa1b4c599164cfc2cc62bddcb8f53e1b3204fc77ac54ce07f";
    private readonly List<Process> _processes = [];
    private readonly List<HttpClient> _clients = [];
    private readonly List<(SyncthingAdapter Adapter, DeviceId DeviceId)> _addedDevices = [];
    private readonly bool _ownsProcesses;

    private SyncthingPair(
        SyncthingAdapter a,
        SyncthingAdapter b,
        DeviceId idA,
        DeviceId idB,
        string root,
        bool ownsProcesses)
    {
        A = a;
        B = b;
        IdA = idA;
        IdB = idB;
        Root = root;
        FolderId = "lansw-c1-c6-" + Guid.NewGuid().ToString("N");
        FolderA = Path.Combine(root, "A", "sync", FolderId);
        FolderB = Path.Combine(root, "B", "sync", FolderId);
        _ownsProcesses = ownsProcesses;
    }

    public SyncthingAdapter A { get; }
    public SyncthingAdapter B { get; }
    public DeviceId IdA { get; }
    public DeviceId IdB { get; }
    public string FolderId { get; }
    public string FolderA { get; }
    public string FolderB { get; }
    public string Root { get; }

    public static async Task<(SyncthingPair? Pair, string? SkipReason)> CreateAsync()
    {
        var external = new[]
        {
            Environment.GetEnvironmentVariable("LANSW_TEST_A_GUI"),
            Environment.GetEnvironmentVariable("LANSW_TEST_A_APIKEY"),
            Environment.GetEnvironmentVariable("LANSW_TEST_B_GUI"),
            Environment.GetEnvironmentVariable("LANSW_TEST_B_APIKEY"),
        };
        var binary = Environment.GetEnvironmentVariable("LANSW_SYNCTHING_BIN");

        if (external.All(value => !string.IsNullOrWhiteSpace(value)))
        {
            var pair = await CreateExternalAsync(external[0]!, external[1]!, external[2]!, external[3]!);
            await pair.PrepareFolderAsync();
            return (pair, null);
        }

        if (external.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            throw new InvalidOperationException("External integration mode requires all four LANSW_TEST_A/B_GUI/APIKEY variables.");
        }

        if (string.IsNullOrWhiteSpace(binary))
        {
            return (null, "No external pair and LANSW_SYNCTHING_BIN is not set.");
        }

        var selfHosted = await CreateSelfHostedAsync(binary);
        await selfHosted.PrepareFolderAsync();
        return (selfHosted, null);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await A.DeleteFolderAsync(FolderId);
        }
        catch (Exception)
        {
        }

        try
        {
            await B.DeleteFolderAsync(FolderId);
        }

        catch (Exception)
        {
        }

        foreach (var (adapter, deviceId) in _addedDevices)
        {
            try
            {
                await adapter.DeleteDeviceAsync(deviceId);
            }
            catch (Exception)
            {
            }
        }
        if (_ownsProcesses)
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        try
        {
            LongPath.DeleteTree(Root);
        }
        catch (Exception)
        {
        }
    }

    public async Task WaitForFileAsync(string path, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (LongPath.Exists(path) && LongPath.ReadAllText(path) == expected)
                {
                    return;
                }
            }
            catch (IOException)
            {
                // Syncthing may briefly hold an exclusive handle while replacing a file.
                // This method is a polling wait, so retry until the original deadline.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"File did not synchronize to {path} within {timeout}.");
    }

    private static async Task<SyncthingPair> CreateExternalAsync(string guiA, string keyA, string guiB, string keyB)
    {
        var root = Path.Combine(Path.GetTempPath(), "LanSync.IntegrationTests", Guid.NewGuid().ToString("N"));
        var pair = CreateAdapters(guiA, keyA, guiB, keyB, root, ownsProcesses: false);
        await pair.ValidateVersionsAsync();
        return pair;
    }

    private static async Task<SyncthingPair> CreateSelfHostedAsync(string binary)
    {
        var fullBinary = Path.GetFullPath(binary);
        if (!File.Exists(fullBinary))
        {
            throw new FileNotFoundException("LANSW_SYNCTHING_BIN does not exist.", fullBinary);
        }

        await using (var stream = File.OpenRead(fullBinary))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            var expectedHash = GetExpectedBinarySha256();
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Syncthing binary SHA-256 mismatch. Expected {expectedHash}, got {hash}.");
            }
        }

        var root = Path.Combine(Path.GetTempPath(), "LanSync.IntegrationTests", Guid.NewGuid().ToString("N"));
        var guiPortA = GetFreePort();
        var guiPortB = GetFreePort();
        var syncPortA = GetFreePort();
        var syncPortB = GetFreePort();
        var keyA = Guid.NewGuid().ToString("N");
        var keyB = Guid.NewGuid().ToString("N");
        LongPath.CreateDirectory(root);
        var clientA = new HttpClient { BaseAddress = EnsureTrailingSlash($"http://127.0.0.1:{guiPortA}"), Timeout = TimeSpan.FromSeconds(90) };
        var clientB = new HttpClient { BaseAddress = EnsureTrailingSlash($"http://127.0.0.1:{guiPortB}"), Timeout = TimeSpan.FromSeconds(90) };
        var adapterA = new SyncthingAdapter(new SyncthingRestClient(clientA, keyA));
        var adapterB = new SyncthingAdapter(new SyncthingRestClient(clientB, keyB));
        var processA = StartProcess(fullBinary, Path.Combine(root, "A", "home"), guiPortA, keyA, Path.Combine(root, "A", "syncthing"));
        var processB = StartProcess(fullBinary, Path.Combine(root, "B", "home"), guiPortB, keyB, Path.Combine(root, "B", "syncthing"));
        await adapterA.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        await adapterB.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        var healthA = await adapterA.GetHealthAsync();
        var healthB = await adapterB.GetHealthAsync();
        var pair = new SyncthingPair(
            adapterA,
            adapterB,
            new DeviceId(healthA.DeviceId ?? throw new InvalidDataException("A did not return myID.")),
            new DeviceId(healthB.DeviceId ?? throw new InvalidDataException("B did not return myID.")),
            root,
            ownsProcesses: true);
        pair._clients.Add(clientA);
        pair._clients.Add(clientB);
        pair._processes.Add(processA);
        pair._processes.Add(processB);
        await pair.ValidateVersionsAsync();

        await pair.ConfigurePrivateInstanceAsync(pair.A, syncPortA);
        await pair.ConfigurePrivateInstanceAsync(pair.B, syncPortB);
        await pair.A.RestartIfRequiredAsync(TimeSpan.FromSeconds(60));
        await pair.B.RestartIfRequiredAsync(TimeSpan.FromSeconds(60));

        await pair.AddDeviceIfMissingAsync(pair.A, pair.IdB, "integration-B", [$"tcp://127.0.0.1:{syncPortB}"]);
        await pair.AddDeviceIfMissingAsync(pair.B, pair.IdA, "integration-A", [$"tcp://127.0.0.1:{syncPortA}"]);
        return pair;
    }

    private async Task AddDeviceIfMissingAsync(
        SyncthingAdapter adapter,
        DeviceId deviceId,
        string name,
        IReadOnlyList<string> addresses)
    {
        var devices = await adapter.GetDevicesAsync();
        var exists = devices.OfType<JsonObject>().Any(device =>
            string.Equals(device["deviceID"]?.GetValue<string>(), deviceId.Value, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            await adapter.AddDeviceAsync(deviceId, name, addresses);
            _addedDevices.Add((adapter, deviceId));
        }
    }

    private static SyncthingPair CreateAdapters(
        string guiA,
        string keyA,
        string guiB,
        string keyB,
        string root,
        bool ownsProcesses)
    {
        LongPath.CreateDirectory(root);
        var clientA = new HttpClient { BaseAddress = EnsureTrailingSlash(guiA), Timeout = TimeSpan.FromSeconds(90) };
        var clientB = new HttpClient { BaseAddress = EnsureTrailingSlash(guiB), Timeout = TimeSpan.FromSeconds(90) };
        var adapterA = new SyncthingAdapter(new SyncthingRestClient(clientA, keyA));
        var adapterB = new SyncthingAdapter(new SyncthingRestClient(clientB, keyB));

        var statusA = adapterA.GetHealthAsync().GetAwaiter().GetResult();
        var statusB = adapterB.GetHealthAsync().GetAwaiter().GetResult();
        var pair = new SyncthingPair(
            adapterA,
            adapterB,
            new DeviceId(statusA.DeviceId ?? throw new InvalidDataException("A did not return myID.")),
            new DeviceId(statusB.DeviceId ?? throw new InvalidDataException("B did not return myID.")),
            root,
            ownsProcesses);
        pair._clients.Add(clientA);
        pair._clients.Add(clientB);
        return pair;
    }

    private async Task PrepareFolderAsync()
    {
        LongPath.CreateDirectory(FolderA);
        LongPath.CreateDirectory(FolderB);
        var devices = new[] { IdA, IdB };
        await A.AddFolderAsync(new FolderSpec(FolderId, "LanSync C1-C6", FolderA, devices));
        await B.AddFolderAsync(new FolderSpec(FolderId, "LanSync C1-C6", FolderB, devices));
        await A.WaitForConnectionAsync(IdB, TimeSpan.FromSeconds(60));
        await B.WaitForConnectionAsync(IdA, TimeSpan.FromSeconds(60));
    }

    private async Task ValidateVersionsAsync()
    {
        var a = await A.GetHealthAsync();
        var b = await B.GetHealthAsync();
        if (a.Version != RequiredVersion || b.Version != RequiredVersion)
        {
            throw new InvalidOperationException($"Integration instances must both be {RequiredVersion}; got A={a.Version}, B={b.Version}.");
        }
    }

    private async Task ConfigurePrivateInstanceAsync(SyncthingAdapter adapter, int syncPort)
    {
        await adapter.UpdateOptionsAsync(new Dictionary<string, JsonNode?>
        {
            ["globalAnnounceEnabled"] = JsonValue.Create(false),
            ["globalAnnounceServers"] = new JsonArray(),
            ["localAnnounceEnabled"] = JsonValue.Create(false),
            ["relaysEnabled"] = JsonValue.Create(false),
            ["listenAddresses"] = new JsonArray($"tcp://127.0.0.1:{syncPort}"),
            ["natEnabled"] = JsonValue.Create(false),
        });
    }

    private static Process StartProcess(string binary, string home, int guiPort, string apiKey, string logPath)
    {
        LongPath.CreateDirectory(home);
        LongPath.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--home");
        startInfo.ArgumentList.Add(home);
        startInfo.ArgumentList.Add("--no-browser");
        startInfo.ArgumentList.Add("--no-upgrade");
        startInfo.ArgumentList.Add("--gui-address");
        startInfo.ArgumentList.Add($"127.0.0.1:{guiPort}");
        startInfo.ArgumentList.Add("--gui-apikey");
        startInfo.ArgumentList.Add(apiKey);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Syncthing.");
        _ = PumpAsync(process.StandardOutput, LongPath.Normalize(logPath + ".out.log"));
        _ = PumpAsync(process.StandardError, LongPath.Normalize(logPath + ".err.log"));
        return process;
    }

    private static async Task PumpAsync(StreamReader reader, string logPath)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await File.AppendAllTextAsync(logPath, line + Environment.NewLine);
        }
    }

    private static Uri EnsureTrailingSlash(string gui) => new(gui.TrimEnd('/') + "/", UriKind.Absolute);

    internal static string GetExpectedBinarySha256()
    {
        var overrideHash = Environment.GetEnvironmentVariable("LANSW_SYNCTHING_SHA256")?.Trim();
        if (!string.IsNullOrEmpty(overrideHash))
        {
            if (overrideHash.Length != 64 || overrideHash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidOperationException("LANSW_SYNCTHING_SHA256 must be a 64-character hexadecimal SHA-256 value.");
            }

            return overrideHash.ToLowerInvariant();
        }

        if (OperatingSystem.IsWindows())
        {
            return RequiredWindowsSha256;
        }

        if (OperatingSystem.IsLinux())
        {
            return RequiredLinuxSha256;
        }

        throw new PlatformNotSupportedException(
            "No built-in Syncthing v2.1.5 SHA-256 is known for this OS. Set LANSW_SYNCTHING_SHA256 explicitly.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
