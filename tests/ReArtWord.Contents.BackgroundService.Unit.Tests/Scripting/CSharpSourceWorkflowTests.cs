using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>実workerからの継続値を実WFエンジンで独立した後続へ渡します。</summary>
public sealed class CSharpSourceWorkflowTests
{
    /// <summary>先の後続が待機中でも次を開始し、Signalと値を同じ一件として保持します。</summary>
    /// <returns>型検査、開始信号、並走中の入力の独立性です。</returns>
    [Fact]
    public async Task StartsIndependentDownstreamWhileEarlierItemWaitsAsync()
    {
        const string code = "public static async IAsyncEnumerable<int> Run() { await Task.Yield(); yield return 1; yield return 2; }";
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code, continuous: true);
        Channel<NodeExecutionContext> entered = Channel.CreateUnbounded<NodeExecutionContext>();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously), done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<int> completed = new();
        WorkflowFixture fixture = new(CSharpSourceTests.Node(), new WorkflowFixture.ActionNode(async (context, token) =>
        {
            int original = context.Inputs["value"].Json.GetInt32();
            await entered.Writer.WriteAsync(context, token).ConfigureAwait(true);
            await release.Task.WaitAsync(token).ConfigureAwait(true);
            Assert.Equal(original, context.Inputs["value"].Json.GetInt32());
            completed.Enqueue(original);
            if (completed.Count == 2)
            {
                done.TrySetResult();
            }
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("w", "Continuous script", [
            new("source", "script.csharp-source", JsonSerializer.SerializeToElement(new { code, analysis }, ContractJson.Options), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            WorkflowFixture.Definition("sink")], [new("source", "value", "sink", "value"), new("source", "start", "sink", "trigger")], true);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await fixture.Sources.ApplyAsync(compiled.Workflow!, deadline.Token).ConfigureAwait(true);
        NodeExecutionContext first = await entered.Reader.ReadAsync(deadline.Token).ConfigureAwait(true);
        NodeExecutionContext second = await entered.Reader.ReadAsync(deadline.Token).ConfigureAwait(true);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.NotEqual(first.Inputs["value"].Json.GetInt32(), second.Inputs["value"].Json.GetInt32());
        release.TrySetResult();
        await done.Task.WaitAsync(deadline.Token).ConfigureAwait(true);
        Assert.Equal([1, 2], completed.Order());
    }
}
