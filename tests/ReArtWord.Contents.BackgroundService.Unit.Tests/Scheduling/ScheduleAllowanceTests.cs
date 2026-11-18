using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>元の予定回の期限を引き継ぎ、担当交代や再起動で新しい許容時間を与えないことを確認します。</summary>
public sealed class ScheduleAllowanceTests
{
    /// <summary>時差のある担当で、期限内・通信時間幅・期限超過・起動実体の不一致を区別します。</summary>
    /// <returns>復帰した対象への初回配送の可否です。</returns>
    [Theory]
    [InlineData(-3600, 8, true, null)]
    [InlineData(3600, 8, true, null)]
    [InlineData(3600, 10, true, "schedule.targetTimingUnconfirmed")]
    [InlineData(-3600, 12, true, "schedule.targetMissed")]
    [InlineData(0, 0, false, "schedule.targetTimingUnconfirmed")]
    public async Task HandoffDoesNotGrantANewTolerance(int offset, int elapsed, bool sameInstance, string? expected)
    {
        ManagementTransport.AdmissionTestClock clock = new();
        WorkflowFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        DateTimeOffset due = clock.GetUtcNow();
        clock.Adjust(TimeSpan.FromSeconds(offset));
        PeerInfo target = new("target", Guid.NewGuid(), "host", "127.0.0.1", 12345, EPeerStatus.Unavailable, 0, null, due);
        fixture.Peers.Others = [target with { Status = EPeerStatus.Ready }];
        ScheduleClockBound bound = new(fixture.Peers.LocalInstanceId, TimeSpan.FromSeconds(9).Ticks, TimeSpan.FromSeconds(11).Ticks);
        Guid current = sameInstance ? bound.Instance
                                    : Guid.NewGuid();
        ScheduledOccurrence schedule = new("timeline", "source", Guid.NewGuid(), due, EScheduleMissedPolicy.Within, TimeSpan.FromMinutes(1))
        { Allowance = bound.Restore(new(current, TimeSpan.FromSeconds(elapsed).Ticks), clock) };
        NodeExecutionContext context = Data.NodeTestContext.Create("{}", NodeValue.Null) with
        {
            Schedule = schedule,
            CapturedTargets = new Dictionary<string, TargetResolution>(StringComparer.Ordinal) { ["node"] = new(EExecutionScope.One, [target]) },
        };
        ScheduledTargetReadiness readiness = new(fixture.Peers, clock, TimeSpan.FromMinutes(5));
        Assert.True(readiness.Track(new(context, NodeResult.Success())));
        RemoteExecutionRequest request = new(Guid.NewGuid(), context.CausationId, context.WorkflowId, context.Revision,
            WorkflowFixture.Definition("node", "test"), context.Inputs, target.PeerId, target.InstanceId, due.AddMinutes(5));
        clock.Adjust(TimeSpan.FromSeconds(-2 * offset));
        ScheduledTargetPreparation prepared = await readiness.PrepareAsync(request, () => false);
        Assert.Equal(expected, prepared.Rejection?.ReasonCode);
    }
}
