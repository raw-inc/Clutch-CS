using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>同じノードの並行実行を、直近の一件が終わっただけで完了扱いにしません。</summary>
public sealed class ExperimentalNodeActivityTests
{
    /// <summary>同じ実行IDの複数ノードと同じノードの複数実行を混同しません。</summary>
    [Fact]
    public void ActiveObservationsKeepOverlappingExecutionsAndFilterTheWorkflow()
    {
        NodeValueObservations observations = new();
        NodeValueObservation first = new(Guid.NewGuid(), Guid.NewGuid(), "flow", "revision", "a", "peer", DateTimeOffset.UtcNow);
        NodeValueObservation anotherNode = first with { NodeId = "b" };
        NodeValueObservation later = first with { ExecutionId = Guid.NewGuid(), StartedAt = first.StartedAt.AddSeconds(1) };
        observations.Record(first);
        observations.Record(anotherNode);
        observations.Record(later);
        observations.Record(later with { CompletedAt = later.StartedAt.AddSeconds(1) });
        Assert.Equal(2, observations.Snapshot("flow", runningOnly: true).Count);
        Assert.Empty(observations.Snapshot("different", runningOnly: true));
        observations.Record(first with { CompletedAt = later.StartedAt.AddSeconds(2) });
        Assert.Equal("b", Assert.Single(observations.Snapshot("flow", runningOnly: true)).NodeId);
        observations.Record(anotherNode with { CompletedAt = later.StartedAt.AddSeconds(3) });
        Assert.Empty(observations.Snapshot("flow", runningOnly: true));
        Assert.Equal(2, observations.Snapshot().Count);
    }
}
