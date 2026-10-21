using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>補償後に元の成功を下流の成功と誤認しないことを確認します。</summary>
public sealed class FileRoutingTests
{
    /// <summary>全体の結果で分岐し、元の実行と補償の両方を入力へ渡します。</summary>
    /// <returns>グラフとtransactionの接続確認です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, "failure")]
    [InlineData(EOutcome.Unknown, "unknown")]
    [InlineData(EOutcome.Succeeded, "success")]
    public async Task CompensationOutcomeDeterminesRouteWithoutRewritingOriginalExecution(EOutcome outcome, string route)
    {
        NodeValue? observed = null;
        WorkflowFixture fixture = new([.. FileSystemNodeRegistration.Create(new FileSystemOptions([Path.GetTempPath()])),
            new WorkflowFixture.ActionNode((context, _) =>
            {
                observed = context.Inputs["value"];
                return ValueTask.FromResult(NodeResult.Success(observed));
            })]);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        fixture.Peers.Others = [new PeerInfo("b", Guid.NewGuid(), "host-b", "127.0.0.2", 0, EPeerStatus.Ready, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow)];
        TransactionDispatcher transactions = new(outcome);
        fixture.Engine.FileTransactions = transactions;
        NodeDefinition file = WorkflowFixture.Definition("write", "file.upsert", """{"path":"routing.txt","failurePolicy":"rollbackAll"}""") with
        {
            Target = new TargetDefinition(EExecutionScope.All, ["a", "b"]),
            Inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("new") },
        };
        CompilationResult compilation = fixture.Compiler.Compile(new WorkflowDefinition("w", "file routing", [file, WorkflowFixture.Definition("result")],
            [new WireDefinition("write", "fileTransaction", "result", "value", route)]));
        Assert.Empty(compilation.Issues);

        ExecutionReport report = await fixture.Engine.ExecuteAsync(compilation.Workflow!, "write").ConfigureAwait(true);

        Assert.Equal(2, transactions.Request!.Targets.Count);
        Assert.NotNull(observed);
        NodeExecutionReport write = report.Nodes.Single(node => string.Equals(node.NodeId, "write", StringComparison.Ordinal));
        Assert.Equal(outcome, write.Outcome);
        Assert.All(write.Targets, item => Assert.Equal(EOutcome.Succeeded, item.Result.Outcome));
        Assert.Equal(transactions.Request.OperationId, write.FileTransaction!.OperationId);
        Assert.Equal(EOutcome.Succeeded, report.Nodes.Single(node => string.Equals(node.NodeId, "result", StringComparison.Ordinal)).Outcome);
    }

    /// <summary>実ファイル操作は別の実UDP試験で確認し、ここでは段階結果を固定します。</summary>
    private sealed class TransactionDispatcher(EOutcome outcome) : IFileTransactionDispatcher
    {
        /// <summary>実行先と要求IDを保持した受付内容です。</summary>
        internal FileTransactionRequest? Request
        {
            get; private set;
        }

        /// <summary>元の実行成功と全体の非成功が共存する境界を再現します。</summary>
        /// <returns>退避や補償を含む全体結果です。</returns>
        public Task<FileTransactionResult> ExecuteAsync(FileTransactionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            FileTargetTransactionResult[] targets = request.Targets.Select(target => new FileTargetTransactionResult(target.TargetPeerId, target.TargetInstanceId, target.Id,
                NodeResult.Success(NodeValue.FromObject("original")), new(EOutcome.Succeeded), new(EOutcome.Succeeded), new(EOutcome.Succeeded))).ToArray();
            return Task.FromResult(new FileTransactionResult(request.OperationId, outcome, targets));
        }
    }
}
