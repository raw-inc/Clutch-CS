using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;
using ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>一件の共通方針が起動時のファイル既定と転送再開を上書きする境界です。</summary>
public sealed class ClusterOperationalFileTests
{
    /// <summary>既定Failでも開始済みWaitのローカルリースは設定更新を待たず保持します。</summary>
    /// <returns>取得済み方針での実書込みです。</returns>
    [Fact]
    public async Task LocalNodeUsesCapturedWaitPolicy()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "value");
        NodeExecutionContext context = FileTestDirectory.Context(new { path }, NodeValue.FromObject("after")) with { Operational = Snapshot(EFileConflictPolicy.Wait) };
        Task<NodeResult> operation;
        using (IDisposable held = await fixture.Service.Operations.AcquireAsync([new(path, true)]).ConfigureAwait(true))
        {
            operation = fixture.Action("file.upsert").ExecuteAsync(context).AsTask();
            await Task.Delay(75).ConfigureAwait(true);
            Assert.False(operation.IsCompleted);
        }
        Assert.Equal(EOutcome.Succeeded, (await operation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        Assert.Equal("after", await File.ReadAllTextAsync(path).ConfigureAwait(true));
    }

    /// <summary>実途中失敗の復元可否はノード個別設定やTOML旧既定から独立します。</summary>
    /// <returns>共通版で選んだ失敗方針の実行結果です。</returns>
    [Theory(Skip = "旧仕様: コピー元を読めない場合でも先に既存先を削除する想定。現在は一時コピーの完成前に既存先を変更しません。CopyReadFailureKeepsExistingDestinationAsyncで現行境界を検証します。")]
    [InlineData(EFileInternalFailurePolicy.Keep, EFileInternalFailurePolicy.Restore, true)]
    [InlineData(EFileInternalFailurePolicy.Restore, EFileInternalFailurePolicy.Keep, false)]
    public async Task InternalFailureUsesCapturedClusterPolicy(EFileInternalFailurePolicy bootstrap, EFileInternalFailurePolicy captured, bool restored)
    {
        using FileTestDirectory fixture = new(failure: bootstrap);
        string source = Path.Combine(fixture.Root, "source");
        string destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string lockedPath = Path.Combine(source, "locked");
        string original = Path.Combine(destination, "original");
        await File.WriteAllTextAsync(lockedPath, "new").ConfigureAwait(true);
        await File.WriteAllTextAsync(original, "before").ConfigureAwait(true);
        using FileStream locked = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        NodeExecutionContext context = FileTestDirectory.Context(new { path = source, destination, overwrite = true, recursive = true }) with { Operational = Snapshot(EFileConflictPolicy.Fail, captured) };
        NodeResult result = await fixture.Action("directory.copy").ExecuteAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal(restored, File.Exists(original));
    }

    /// <summary>コピー元の読取り失敗は確定前なので、保持・復元の方針にかかわらず既存先を保ちます。</summary>
    /// <returns>既存ファイルの内容と、一時コピーの回収です。</returns>
    [Theory]
    [InlineData(EFileInternalFailurePolicy.Keep, EFileInternalFailurePolicy.Restore)]
    [InlineData(EFileInternalFailurePolicy.Restore, EFileInternalFailurePolicy.Keep)]
    public async Task CopyReadFailureKeepsExistingDestinationAsync(EFileInternalFailurePolicy bootstrap, EFileInternalFailurePolicy captured)
    {
        using FileTestDirectory fixture = new(failure: bootstrap);
        string source = Path.Combine(fixture.Root, "source");
        string destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string lockedPath = Path.Combine(source, "locked");
        string original = Path.Combine(destination, "original");
        await File.WriteAllTextAsync(lockedPath, "new");
        await File.WriteAllTextAsync(original, "before");
        using FileStream locked = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        NodeExecutionContext context = FileTestDirectory.Context(new { path = source, destination, overwrite = true, recursive = true })
            with { Operational = Snapshot(EFileConflictPolicy.Fail, captured) };
        NodeResult result = await fixture.Action("directory.copy").ExecuteAsync(context);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("before", await File.ReadAllTextAsync(original));
        Assert.False(File.Exists(Path.Combine(destination, "locked")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*.partial"));
    }

    /// <summary>再作成したサービスと新要求の方針が違っても既存段階は開始時方針を使います。</summary>
    /// <returns>durable 段階と実コミットの確認です。</returns>
    [Fact]
    public async Task ResumedStageKeepsOriginalPolicyAcrossServiceRecreation()
    {
        using FileTestDirectory fixture = new();
        byte[] bytes = [1, 2, 3];
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string destination = Path.Combine(fixture.Root, "received");
        FileStageRequest request = new("transfer", "source", "/source", destination, FileTransferManifest.ComputeSha256([new(string.Empty, false, bytes.Length, hash)]), false, Operational: Snapshot(EFileConflictPolicy.Wait));
        FileStageToken stage = await fixture.Service.OpenStageAsync(request).ConfigureAwait(true);
        await fixture.Service.WriteChunkAsync(new(stage, string.Empty, 0, bytes, hash, bytes.Length, hash)).ConfigureAwait(true);
        FileTransferService restarted = new(fixture.Options);
        await Assert.ThrowsAnyAsync<IOException>(() => restarted.OpenStageAsync(request with { Operational = Snapshot(EFileConflictPolicy.Fail) })).ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<IOException>(() => restarted.OpenStageAsync(request with { Operational = Snapshot(EFileConflictPolicy.Wait, EFileInternalFailurePolicy.Restore) })).ConfigureAwait(true);
        FileStageToken reopened = await restarted.OpenStageAsync(request with { Operational = Snapshot(EFileConflictPolicy.Wait, zone: "Asia/Tokyo") }).ConfigureAwait(true);
        Assert.Equal(stage, reopened);
        Task<FileStageCommitResult> commit;
        using (IDisposable held = await restarted.Operations.AcquireAsync([new(destination, true)]).ConfigureAwait(true))
        {
            commit = restarted.CommitStageAsync(reopened);
            await Task.Delay(75).ConfigureAwait(true);
            Assert.False(commit.IsCompleted);
        }
        Assert.Equal(bytes.Length, (await commit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
    }

    /// <summary>保持段階の二方針を変更したノード実行は明示NotExecutedです。</summary>
    /// <returns>変更前の転送内容が維持される確認です。</returns>
    [Fact]
    public async Task ChangedStagePolicyIsNotExecuted()
    {
        using FileTestDirectory fixture = new();
        string sourcePath = Path.Combine(fixture.Root, "source");
        string path = Path.Combine(fixture.Root, "destination");
        await File.WriteAllTextAsync(sourcePath, "original").ConfigureAwait(true);
        NodeExecutionContext context = FileTestDirectory.Context(new { sourcePeer = "peer", sourcePath, path, requestId = "fixed" }) with { Operational = Snapshot(EFileConflictPolicy.Wait) };
        IActionNode node = fixture.Action("file.transfer");
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        NodeExecutionContext changed = context with { ExecutionId = Guid.NewGuid(), Operational = Snapshot(EFileConflictPolicy.Fail) };
        NodeResult rejected = await node.ExecuteAsync(changed).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Equal("file.stage.policyMismatch", rejected.ReasonCode);
        Assert.Equal("original", await File.ReadAllTextAsync(path).ConfigureAwait(true));
    }

    /// <summary>共通予約もローカル既定Failではなく一件のWaitを使います。</summary>
    /// <returns>競合する二操作が解放後に完了する確認です。</returns>
    [Fact]
    public async Task ClusterReservationUsesCapturedPolicy()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        FileTestPeer peer = await FileTestPeer.CreateAsync(Guid.NewGuid().ToString("N"), "peer", port, true, node =>
        {
            string upsert = "file.upsert";
            if (!string.Equals(node.Descriptor.Type, upsert, StringComparison.Ordinal))
            {
                return node;
            }
            return new ControlledFileNode((ICompensableFileNode)node)
            {
                BeforeExecution = async token =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token).ConfigureAwait(false);
                    }
                    return null;
                },
            };
        });
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        IActionNode action = (IActionNode)peer.Registry.Find("file.upsert")!;
        string path = Path.Combine(peer.Root, "shared");
        NodeExecutionContext first = FileTestDirectory.Context(new { path }, NodeValue.FromObject("first")) with { Operational = Snapshot(EFileConflictPolicy.Wait) };
        NodeExecutionContext second = FileTestDirectory.Context(new { path }, NodeValue.FromObject("second")) with { Operational = first.Operational };
        Task<NodeResult> running = peer.Transactions.ExecuteFileAsync(action, first).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        Task<NodeResult> waiting = peer.Transactions.ExecuteFileAsync(action, second).AsTask();
        try
        {
            await Task.Delay(100).ConfigureAwait(true);
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(EOutcome.Succeeded, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>一件に渡す内容版を生成します。</summary>
    /// <returns>検査値付きの共通設定です。</returns>
    private static ClusterOperationalSnapshot Snapshot(EFileConflictPolicy conflict, EFileInternalFailurePolicy failure = EFileInternalFailurePolicy.Keep, string zone = "UTC")
    {
        ClusterOperationalSettings settings = new(zone, conflict, failure, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        return new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow);
    }
}
