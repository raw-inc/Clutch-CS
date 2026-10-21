using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>大きい結果を繰り返しても件数上限まで無条件に蓄積しないことを確認します。</summary>
public sealed class ExecutionRetentionTests
{
    /// <summary>過去の診断履歴を退避しても実行自体の成功は変わりません。</summary>
    /// <returns>実行結果と有限保持の確認です。</returns>
    [Fact]
    public async Task LargeResultsEvictOldDiagnosticsWhileReturningActualOutcome()
    {
        byte[] payload = new byte[1024 * 1024];
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(NodeValue.FromBytes(payload)))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("w", "retention", [WorkflowFixture.Definition("n")], [])).Workflow!;
        ExecutionReport first = await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
        ExecutionReport latest = first;
        for (int index = 0; index < 70; index++)
        {
            latest = await fixture.Engine.ExecuteAsync(plan).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, latest.Outcome);
        }
        Assert.Null(fixture.Engine.Find(first.Id));
        Assert.NotNull(fixture.Engine.Find(latest.Id));
        Assert.InRange(fixture.Engine.Recent().Count, 1, 69);
    }
}
