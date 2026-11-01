using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Nodes.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>元起動の待機を別の起動回の成功で満たさないことを実プロセスで検証します。</summary>
public sealed class ProcessReadyIdentityTests
{
    /// <summary>初回観測と前段出力のどちらも起動回を固定します。</summary>
    /// <returns>独立プロセスの再起動を含む試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ReplacementReadyDoesNotCompleteOriginalWait()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await service.RegisterAsync(scope.Definition);
        ProcessOperationResult first = await service.StartAsync(scope.Definition.Id);
        scope.Track(first);
        IActionNode node = (IActionNode)Assert.Single(ProcessNodeRegistration.Create(service, new WorkflowEventHub()), candidate => string.Equals(candidate.Descriptor.Type, "process.waitReady", StringComparison.Ordinal));

        // 起動回の違いを検証するため、実プロセスの停止・再起動とready期限を競合させません。
        string settings = JsonSerializer.Serialize(new { processId = scope.Definition.Id, timeoutMs = (int?)null });
        Task<NodeResult> pending = node.ExecuteAsync(NodeTestContext.Create(settings), deadline.Token).AsTask();
        try
        {
            await service.StopAsync(scope.Definition.Id, EProcessStopPolicy.Force, 10, deadline.Token);
            ProcessOperationResult second = await service.StartAsync(scope.Definition.Id, deadline.Token);
            scope.Track(second);
            await service.SignalAsync(scope.Definition.Id, new ProcessSignal(second.Status!.InstanceId, true, true), deadline.Token);
            Assert.Equal("process.readyInstanceChanged", (await pending.ConfigureAwait(true)).ReasonCode);
            NodeValue old = NodeValue.FromObject(new { id = scope.Definition.Id, instanceId = first.Status!.InstanceId });
            Assert.Equal("process.readyInstanceChanged", (await node.ExecuteAsync(NodeTestContext.Create(settings, old), deadline.Token)).ReasonCode);
            NodeValue current = NodeValue.FromObject(new { id = scope.Definition.Id, instanceId = second.Status.InstanceId });
            Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(NodeTestContext.Create(settings, current), deadline.Token)).Outcome);
            NodeValue wrongApplication = NodeValue.FromObject(new { id = "different", instanceId = second.Status.InstanceId });
            Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(NodeTestContext.Create(settings, wrongApplication), deadline.Token)).Outcome);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(true);
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
    }
}
