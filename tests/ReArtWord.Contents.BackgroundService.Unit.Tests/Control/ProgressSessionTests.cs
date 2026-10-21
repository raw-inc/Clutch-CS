using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>同じノードでもWFの実行・反復・所有実体が異なる進捗を混ぜません。</summary>
public sealed class ProgressSessionTests
{
    /// <summary>並走する二実行と同じ実行の二要素を独立に表示し、終了対象だけを回収します。</summary>
    [Fact]
    public void CompletionRemovesOnlyItsRunAndScopeAndRejectsLateUpdates()
    {
        NodePreviewStore store = new();
        ProgressSession first = new(Guid.NewGuid(), "owner", Guid.NewGuid(), DateTimeOffset.UtcNow);
        ProgressSession second = first with { RunId = Guid.NewGuid() };
        ProgressSession element = first with { ScopeId = Guid.NewGuid() };
        NodeExecutionContext context = NodeTestContext.Create();
        store.PublishProgress(context with { Progress = first }, new(10, 1));
        store.PublishProgress(context with { Progress = second }, new(20, 2));
        store.PublishProgress(context with { Progress = element }, new(30, 3));
        Assert.Equal(3, store.Read(new("workflow", ["node"])).Count);
        store.PublishProgress(context with { Progress = first }, new(10, 7));
        Assert.Equal(7, store.Read(new("workflow", ["node"])).Single(item => item.Session == first).Progress!.Current);
        store.CompleteProgress(element);
        store.PublishProgress(context with { Progress = element }, new(30, 30));
        Assert.Equal(2, store.Read(new("workflow", ["node"])).Count);
        store.CompleteProgress(first);
        store.PublishProgress(context with { Progress = first }, new(10, 10));
        Assert.Equal(second, Assert.Single(store.Read(new("workflow", ["node"]))).Session);
        store.CompleteProgress(second with { OwnerInstanceId = Guid.NewGuid() });
        Assert.Single(store.Read(new("workflow", ["node"])));
        store.CompleteProgress(second);
        Assert.Empty(store.Read(new("workflow", ["node"])));
    }

    /// <summary>大量の進捗でも有限件数と小さい数値だけを保持します。</summary>
    [Fact]
    public void SessionsAreBoundedAndDoNotCreatePreviewHistory()
    {
        NodePreviewStore store = new();
        NodeExecutionContext context = NodeTestContext.Create();
        for (int index = 0; index < 1500; index++)
        {
            ProgressSession session = new(Guid.NewGuid(), "owner", Guid.NewGuid(), DateTimeOffset.UtcNow);
            store.PublishProgress(context with { Progress = session }, new(1500, index));
        }
        Assert.Equal(NodePreviewStore.MaximumProgressSessions, store.Read(new("workflow", ["node"])).Count);
        Assert.Empty(store.ReadHistory(new("workflow", "node")));
    }
}
