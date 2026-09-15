using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class TemporaryFilesTests
{
    [TestMethod]
    public async Task Temporary_files_are_listed_recursively_without_deleting_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "LanSync.TempFiles.Tests", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        var temporary = Path.Combine(nested, "~syncthing~CaseTest.txt.tmp");
        var unrelated = Path.Combine(nested, "ordinary.tmp");
        Directory.CreateDirectory(Extended(nested));
        File.WriteAllText(Extended(temporary), "temporary");
        File.WriteAllText(Extended(unrelated), "ordinary");

        try
        {
            var (adapter, handler) = TestAdapter.Create();
            handler.EnqueueJson($"{{\"id\":\"folder\",\"path\":{System.Text.Json.JsonSerializer.Serialize(root)}}}");

            var files = await adapter.GetTemporaryFilesAsync("folder");

            Assert.HasCount(1, files);
            Assert.AreEqual("nested/~syncthing~CaseTest.txt.tmp", files[0].RelativePath);
            Assert.AreEqual(9L, files[0].Length);
            Assert.IsTrue(File.Exists(Extended(temporary)), "Read-only enumeration must not delete the residual file.");
            Assert.AreEqual(HttpMethod.Get, handler.Requests.Single().Method);
        }
        finally
        {
            Directory.Delete(Extended(root), recursive: true);
        }
    }

    private static string Extended(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }
}
