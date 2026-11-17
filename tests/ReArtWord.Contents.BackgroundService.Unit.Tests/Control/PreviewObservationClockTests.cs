using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>採取日時の補正と、プレビュー本文・進捗の保持期間を分離します。</summary>
public sealed class PreviewObservationClockTests
{
    /// <summary>UTCの一時間の補正では回収せず、実際の5分経過で本文と進捗を回収します。</summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void MediaAndProgressExpireByElapsedTime(int correctionSeconds)
    {
        ResourceSelectionClock clock = new();
        NodePreviewStore store = new(clock);
        NodeExecutionContext context = NodeTestContext.Create();
        store.Publish(context, "text/plain", "value"u8.ToArray());
        store.PublishProgress(context with { NodeId = "progress" }, new(10, 1));
        NodePreviewSnapshot first = Assert.Single(store.Read(new("workflow", ["node"])));
        clock.Adjust(TimeSpan.FromSeconds(correctionSeconds));
        clock.Advance(TimeSpan.FromSeconds(299));
        NodePreviewSnapshot older = Assert.Single(store.Read(new("workflow", ["node"])));
        Assert.Equal(first.Id, older.Id);
        Assert.Equal(first.CapturedAt, older.CapturedAt);
        Assert.Equal(TimeSpan.FromSeconds(299).Ticks, older.CaptureAgeTicks);
        Assert.Single(store.ReadHistory(new("workflow", "node")));
        Assert.Single(store.Read(new("workflow", ["progress"])));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(store.Read(new("workflow", ["node", "progress"])));
        Assert.Empty(store.ReadHistory(new("workflow", "node")));
        Assert.Throws<KeyNotFoundException>(() => store.ReadChunk(new(first.Id, 0)));
    }

    /// <summary>時計を進めても終了済み進捗の遅延通知を再表示せず、従来の5分保持を維持します。</summary>
    [Fact]
    public void ClockCorrectionCannotDiscardCompletedProgressGuard()
    {
        ResourceSelectionClock clock = new();
        NodePreviewStore store = new(clock);
        ProgressSession session = new(Guid.NewGuid(), "owner", Guid.NewGuid(), clock.GetUtcNow());
        NodeExecutionContext context = NodeTestContext.Create() with { Progress = session };
        store.PublishProgress(context, new(10, 1));
        store.CompleteProgress(session);
        clock.Adjust(TimeSpan.FromHours(1));
        store.PublishProgress(context, new(10, 2));
        Assert.Empty(store.Read(new("workflow", ["node"])));
        clock.Advance(TimeSpan.FromMinutes(5));
        store.PublishProgress(context, new(10, 3));
        Assert.Single(store.Read(new("workflow", ["node"])));
    }
}
