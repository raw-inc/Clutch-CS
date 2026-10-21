using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>実ファイルと実UDPで全対象補償と一回性を検証します。</summary>
[Collection("Performance")]
public sealed class FileTransactionTests
{
    /// <summary>明示離脱した一台の補償付き操作は共有済み状態を作りません。</summary>
    /// <returns>独立運転の検証完了。</returns>
    [Fact]
    public async Task DetachedLocalTransactionUsesOnlyPeerStorage()
    {
        FileTestPeer peer = new(Guid.NewGuid().ToString("N"), "one", Port(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        peer.Cluster.LocalDetachedExecutionAllowed = request => request.Kind.StartsWith("file.", StringComparison.Ordinal) || string.Equals(request.Kind, "node.execute", StringComparison.Ordinal);
        await peer.Cluster.StartAsync();
        await peer.Cluster.DetachAsync();
        string path = Path.Combine(peer.Root, "value.txt");
        await File.WriteAllTextAsync(path, "before");
        FileTransactionRequest request = new(Guid.NewGuid(), [Request(peer, path)]);
        FileTransactionResult result = await peer.Transactions.ExecuteAsync(request);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal("after", await File.ReadAllTextAsync(path));
        Assert.Null(await peer.Repository.ReadAsync("file:transactions", request.OperationId.ToString("N")));
        Assert.NotNull(await peer.Repository.ReadAsync("peer:one:file-transactions", request.OperationId.ToString("N")));
    }

    /// <summary>実行中の結果不明では予約・退避を保持し、照会回復でも本体を再実行しません。</summary>
    /// <returns>実タスクと実ファイルの検証完了。</returns>
    [Fact]
    public async Task UnknownExecutionRetainsReservationUntilReadOnlyRecovery()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledFileNode? firstNode = null;
        FileTestPeer first = new(id, "first", port, true, node =>
        {
            string upsert = "file.upsert";
            if (!string.Equals(node.Descriptor.Type, upsert, StringComparison.Ordinal))
            {
                return node;
            }
            firstNode = new ControlledFileNode((ICompensableFileNode)node)
            {
                BeforeExecution = async token =>
                {
                    entered.TrySetResult();
                    await finish.Task.WaitAsync(token).ConfigureAwait(false);
                    return null;
                },
            };
            return firstNode;
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await first.Cluster.StartAsync();
        FileTestPeer second = new(id, "second", port, false, node =>
        {
            string upsert = "file.upsert";
            if (!string.Equals(node.Descriptor.Type, upsert, StringComparison.Ordinal))
            {
                return node;
            }
            return new ControlledFileNode((ICompensableFileNode)node)
            {
                BeforeExecution = token =>
                {
                    secondEntered.TrySetResult();
                    return Task.FromResult<NodeResult?>(NodeResult.Failure("test.failed", "failure"));
                },
            };
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await second.Cluster.StartAsync();
        await ReadyAsync(first, second);
        string firstPath = Path.Combine(first.Root, "value.txt");
        string secondPath = Path.Combine(second.Root, "value.txt");
        await File.WriteAllTextAsync(firstPath, "original");
        await File.WriteAllTextAsync(secondPath, "original");
        FileTransactionRequest request = new(Guid.NewGuid(), [Request(first, firstPath), Request(second, secondPath)]);
        using CancellationTokenSource caller = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<FileTransactionResult> pending = first.Transactions.ExecuteAsync(request, caller.Token);
        try
        {
            await Task.WhenAll(entered.Task, secondEntered.Task).WaitAsync(timeout.Token);
            await caller.CancelAsync();
            Assert.Equal(EOutcome.Unknown, (await pending.ConfigureAwait(true)).Outcome);
            Assert.Single((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active);
            Assert.NotEmpty(Directory.EnumerateDirectories(Path.Combine(first.Root, "recovery")));
            Assert.Equal(EOutcome.Unknown, (await first.Transactions.RecoverAsync(request.OperationId, timeout.Token))!.Outcome);
            Assert.Equal(1, firstNode!.Executions);
        }
        finally
        {
            finish.TrySetResult();
        }
        foreach (RemoteExecutionRequest execution in request.Targets)
        {
            ClusterRequest command = new(execution.Id, "node.execute", execution.TargetPeerId, execution.TargetInstanceId, execution.ExpiresAt, NodeValue.FromObject(execution));
            ClusterRequestStatus status = await first.Cluster.QueryRequestAsync(command, timeout.Token);
            while (status.Running || status.Response?.Value is null)
            {
                await Task.Delay(20, timeout.Token);
                status = await first.Cluster.QueryRequestAsync(command, timeout.Token);
            }
        }
        FileTransactionResult recovered = (await first.Transactions.RecoverAsync(request.OperationId, timeout.Token))!;
        Assert.True(recovered.Outcome == EOutcome.Failed, JsonSerializer.Serialize(recovered));
        Assert.All(recovered.Targets, target => Assert.Equal(EOutcome.Succeeded, target.Compensation.Outcome));
        Assert.Equal("original", await File.ReadAllTextAsync(firstPath));
        Assert.Equal(1, firstNode!.Executions);
        Assert.Empty((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active);
    }

    /// <summary>一対象の失敗で成功済み対象も復元し、捕捉不能なら実行前に解放します。</summary>
    /// <returns>実ファイルの検証完了。</returns>
    [Theory]
    [InlineData(false, EOutcome.Failed)]
    [InlineData(true, EOutcome.Failed)]
    [InlineData(false, EOutcome.Unknown)]
    public async Task FailureRestoresAllTargetsOrReleasesBeforeExecution(bool captureFailure, EOutcome failureOutcome)
    {
        string id = Guid.NewGuid().ToString("N");
        int port = Port();
        ControlledFileNode? controlled = null;
        FileTestPeer first = new(id, "first", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(false);
        await first.Cluster.StartAsync();
        FileTestPeer second = new(id, "second", port, false, node =>
        {
            string upsert = "file.upsert";
            if (!string.Equals(node.Descriptor.Type, upsert, StringComparison.Ordinal))
            {
                return node;
            }
            controlled = new ControlledFileNode((ICompensableFileNode)node) { FailCapture = captureFailure, BeforeExecution = token => Task.FromResult<NodeResult?>(NodeResult.Failure("test.failed", "failure", failureOutcome)) };
            return controlled;
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(false);
        await second.Cluster.StartAsync();
        await ReadyAsync(first, second);
        string firstPath = Path.Combine(first.Root, "value.txt");
        string secondPath = Path.Combine(second.Root, "value.txt");
        await File.WriteAllTextAsync(firstPath, "before-first");
        await File.WriteAllTextAsync(secondPath, "before-second");
        FileTransactionRequest request = new(Guid.NewGuid(), [Request(first, firstPath), Request(second, secondPath)]);
        FileTransactionResult result = await first.Transactions.ExecuteAsync(request);
        if (captureFailure)
        {
            Assert.Equal(EOutcome.NotExecuted, result.Outcome);
            Assert.All(result.Targets, target => Assert.Equal(EOutcome.NotExecuted, target.Execution.Outcome));
            Assert.Equal(0, controlled!.Executions);
        }
        else
        {
            Assert.Equal(EOutcome.Failed, result.Outcome);
            Assert.All(result.Targets, target => Assert.Equal(EOutcome.Succeeded, target.Compensation.Outcome));
            Assert.All(result.Targets, target => Assert.Equal(EOutcome.Succeeded, target.Release.Outcome));
            Assert.Equal(1, controlled!.Executions);
            Assert.Equal(failureOutcome, result.Targets[1].Execution.Outcome);
        }
        Assert.Equal("before-first", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("before-second", await File.ReadAllTextAsync(secondPath));
        Assert.Empty((await FileReservationLedger.ReadAsync(first.Repository, "file:reservations")).Active);
        Assert.Equal(result.Outcome, (await first.Transactions.ExecuteAsync(request)).Outcome);
    }

    /// <summary>元要求の実行IDと設定を固定します。</summary>
    /// <returns>ファイル変更要求。</returns>
    private static RemoteExecutionRequest Request(FileTestPeer peer, string path) => new(Guid.NewGuid(), Guid.NewGuid(), "demo", "revision", new NodeDefinition("write", "file.upsert", JsonSerializer.SerializeToElement(new { path, failurePolicy = "rollbackAll" })), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("after") }, peer.Cluster.LocalPeerId, peer.Cluster.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));

    /// <summary>両Peerの実同期完了を確認します。</summary>
    /// <returns>Ready成立。</returns>
    private static async Task ReadyAsync(FileTestPeer first, FileTestPeer second)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (first.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2 || second.Cluster.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) != 2)
        {
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>独立した試験ポートです。</summary>
    /// <returns>UDP port。</returns>
    private static int Port()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
