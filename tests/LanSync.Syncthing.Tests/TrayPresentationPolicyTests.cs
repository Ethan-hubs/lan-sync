using LanSync.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanSync.Syncthing.Tests;

[TestClass]
public sealed class TrayPresentationPolicyTests
{
    private static readonly DeviceId Peer =
        new("AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH");

    [TestMethod]
    public void Status_light_uses_connection_kind_paused_as_its_only_paused_source()
    {
        Assert.AreEqual(TrayStatusLight.Direct, TrayStatusPolicy.FromConnectionKind(ConnectionKind.Direct));
        Assert.AreEqual(TrayStatusLight.Relay, TrayStatusPolicy.FromConnectionKind(ConnectionKind.Relay));
        Assert.AreEqual(TrayStatusLight.Offline, TrayStatusPolicy.FromConnectionKind(ConnectionKind.Offline));
        Assert.AreEqual(TrayStatusLight.Paused, TrayStatusPolicy.FromConnectionKind(ConnectionKind.Paused));
        Assert.AreEqual(
            TrayStatusLight.Paused,
            TrayStatusPolicy.Aggregate([ConnectionKind.Paused, ConnectionKind.Paused]));
        Assert.AreEqual(
            TrayStatusLight.Direct,
            TrayStatusPolicy.Aggregate([ConnectionKind.Paused, ConnectionKind.Direct]));
    }

    [TestMethod]
    public void Degraded_restore_requires_the_explicit_unpaused_peer_warning()
    {
        var result = Result(peerPaused: false, durabilityVerified: false, unpausedDevices: [Peer]);

        var presentation = RestorePresentationPolicy.Create(result);

        Assert.IsTrue(presentation.RequiresWarning);
        Assert.AreEqual(RestorePresentationPolicy.UnpausedPeerWarning, presentation.Warning);
        Assert.AreEqual("已在本地执行恢复", presentation.Summary);
        CollectionAssert.AreEqual(new[] { Peer }, presentation.UnpausedDevices.ToArray());
    }

    [TestMethod]
    public void Durable_restore_is_the_only_success_without_a_warning()
    {
        var presentation = RestorePresentationPolicy.Create(
            Result(peerPaused: true, durabilityVerified: true, unpausedDevices: []));

        Assert.IsFalse(presentation.RequiresWarning);
        Assert.AreEqual("已恢复成功", presentation.Summary);
        Assert.IsNull(presentation.Warning);
    }

    [TestMethod]
    public void Unverified_restore_cannot_be_presented_as_durable_success()
    {
        var presentation = RestorePresentationPolicy.Create(
            Result(peerPaused: true, durabilityVerified: false, unpausedDevices: []));

        Assert.IsTrue(presentation.RequiresWarning);
        Assert.AreNotEqual("已恢复成功", presentation.Summary);
        Assert.IsNotNull(presentation.Warning);
    }

    private static VersionRestoreResult Result(
        bool peerPaused,
        bool durabilityVerified,
        IReadOnlyList<DeviceId> unpausedDevices) =>
        new(
            "folder",
            "file.txt",
            "20260920-010203",
            PausedDevices: [],
            ResumedDevices: [],
            peerPaused,
            unpausedDevices,
            durabilityVerified,
            Succeeded: true);
}
