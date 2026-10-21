using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>明示離脱と不意の喪失を安全判断で区別します。</summary>
public sealed class SafetyMembershipTests
{
    /// <summary>参加解除したPeerを縮退扱いせず、復帰待ちのPeerは保持します。</summary>
    [Fact]
    public void ExplicitDetachmentIsNotAnUnexpectedMissingParticipant()
    {
        WorkflowFixture.TestPeers peers = new();
        PeerInfo second = new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow);
        PeerInfo third = second with { PeerId = "c", InstanceId = Guid.NewGuid(), Status = EPeerStatus.Unavailable };
        peers.Others = [second, third];
        NodeSafetyGate safety = new(peers);
        NodeDefinition node = WorkflowFixture.Definition("n") with { Safety = new(StopWhenDegraded: true) };
        Assert.Equal("safety.degraded", safety.GetAutomaticStopReason(node));
        peers.Others = [second, third with { Status = EPeerStatus.Detached }];
        Assert.Null(safety.GetAutomaticStopReason(node));
        peers.Others = [second with { Status = EPeerStatus.Detached }, third with { Status = EPeerStatus.Detached }];
        Assert.Equal("safety.singleton", safety.GetAutomaticStopReason(node));
        Assert.Null(safety.GetAutomaticStopReason(node with { Safety = node.Safety! with { StopWhenSingleton = false } }));
    }
}
