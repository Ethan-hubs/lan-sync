using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.IntegrationTests;

[TestClass]
public sealed class Sha256SelectionTests
{
    [TestMethod]
    public void Override_hash_is_normalized_and_used()
    {
        const string expected = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        var original = Environment.GetEnvironmentVariable("LANSW_SYNCTHING_SHA256");
        try
        {
            Environment.SetEnvironmentVariable("LANSW_SYNCTHING_SHA256", expected);
            Assert.AreEqual(expected.ToLowerInvariant(), SyncthingPair.GetExpectedBinarySha256());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LANSW_SYNCTHING_SHA256", original);
        }
    }

    [TestMethod]
    public void Invalid_override_hash_is_rejected()
    {
        var original = Environment.GetEnvironmentVariable("LANSW_SYNCTHING_SHA256");
        try
        {
            Environment.SetEnvironmentVariable("LANSW_SYNCTHING_SHA256", "not-a-sha256");
            _ = Assert.ThrowsExactly<InvalidOperationException>(SyncthingPair.GetExpectedBinarySha256);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LANSW_SYNCTHING_SHA256", original);
        }
    }
}
