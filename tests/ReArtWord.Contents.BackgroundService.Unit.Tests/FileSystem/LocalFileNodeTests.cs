using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>ローカルファイルの存在前提と補償を実体で確認します。</summary>
public sealed class LocalFileNodeTests
{
    /// <summary>他Peerのパスを接続Peerの許可ルートで保存拒否しません。</summary>
    [Fact]
    public void ValidationDoesNotResolveAnotherPeersEnvironment()
    {
        using FileTestDirectory fixture = new();
        NodeDefinition copy = new("copy", "file.copy", System.Text.Json.JsonSerializer.SerializeToElement(new { path = "/other-peer/source", destination = "/other-peer/destination" }));
        Assert.Empty(fixture.Action("file.copy").Validate(copy));
        NodeDefinition transfer = new("transfer", "file.transfer", System.Text.Json.JsonSerializer.SerializeToElement(new { sourcePeer = "other-peer", sourcePath = "/other-peer/source", path = "/destination-peer/output", requestId = "request" }));
        Assert.Empty(fixture.Action("file.transfer").Validate(transfer));
    }

    /// <summary>create、replace、appendと明示read上限を検証します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task WritesHaveExplicitExistenceSemanticsAndBoundedReadsAsync()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "value.bin");
        NodeExecutionContext context = FileTestDirectory.Context(new { path }, NodeValue.FromObject("one"));
        Assert.Equal(EOutcome.Failed, (await fixture.Action("file.replace").ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Failed, (await fixture.Action("file.append").ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.create").ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.create").ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.append").ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        NodeResult read = await fixture.Action("file.read").ExecuteAsync(FileTestDirectory.Context(new { path, maxBytes = 6 })).ConfigureAwait(true);
        Assert.Equal("oneone", Encoding.UTF8.GetString(read.Outputs["value"].Binary.Span));
        Assert.Equal(EOutcome.Failed, (await fixture.Action("file.read").ExecuteAsync(FileTestDirectory.Context(new { path, maxBytes = 5 })).ConfigureAwait(true)).Outcome);
    }

    /// <summary>明示overwrite=falseの既存宛先を保護し、両パスの許可範囲も確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CopyRequiresOverwriteAndChecksBothRootsAsync()
    {
        using FileTestDirectory fixture = new();
        using FileTestDirectory outside = new();
        string path = Path.Combine(fixture.Root, "source");
        string destination = Path.Combine(fixture.Root, "destination");
        await File.WriteAllTextAsync(path, "source").ConfigureAwait(true);
        await File.WriteAllTextAsync(destination, "prior").ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Action("file.copy").ExecuteAsync(FileTestDirectory.Context(new { path, destination, overwrite = false })).ConfigureAwait(true)).Outcome);
        Assert.Equal("prior", await File.ReadAllTextAsync(destination).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.copy").ExecuteAsync(FileTestDirectory.Context(new { path, destination, overwrite = true })).ConfigureAwait(true)).Outcome);
        Assert.Equal("source", await File.ReadAllTextAsync(destination).ConfigureAwait(true));
        string escaped = Path.Combine(outside.Root, "escaped");
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Action("file.copy").ExecuteAsync(FileTestDirectory.Context(new { path, destination = escaped })).ConfigureAwait(true)).Outcome);
        Assert.False(File.Exists(escaped));
    }

    /// <summary>全対象prepareの退避は内容と元の不存在を復元します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CaptureRestorePreservesOriginalAndReleasesConflictAsync()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "original");
        await File.WriteAllTextAsync(path, "before").ConfigureAwait(true);
        ICompensableFileNode node = (ICompensableFileNode)fixture.Action("file.upsert");
        NodeExecutionContext context = FileTestDirectory.Context(new { path, failurePolicy = "rollbackAll" }, NodeValue.FromObject("after"));
        FileCompensationToken token = await node.CaptureAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal("after", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        NodeExecutionContext conflicting = FileTestDirectory.Context(new { path }, NodeValue.FromObject("conflict"));
        Assert.Equal(EOutcome.NotExecuted, (await node.ExecuteAsync(conflicting).ConfigureAwait(true)).Outcome);
        await node.RestoreAsync(token).ConfigureAwait(true);
        Assert.Equal("before", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        await node.ReleaseAsync(token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(conflicting).ConfigureAwait(true)).Outcome);
    }

    /// <summary>同じ読取りと別subtreeは並行し、待機後は存在前提を再確認します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ConflictWaitRechecksDestinationAndAllowsIndependentReadsAsync()
    {
        using FileTestDirectory fixture = new(EFileConflictPolicy.Wait);
        string path = Path.Combine(fixture.Root, "target");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<NodeResult>? pending = null;
        try
        {
            using (IDisposable write = await fixture.Service.Operations.AcquireAsync([new(path, true)], timeout.Token).ConfigureAwait(true))
            {
                pending = fixture.Action("file.create").ExecuteAsync(FileTestDirectory.Context(new { path, existingFileMode = "error" }, NodeValue.FromObject("new")), timeout.Token).AsTask();
                Assert.False(pending.IsCompleted);
                string other = Path.Combine(fixture.Root, "other");
                using IDisposable readOne = await fixture.Service.Operations.AcquireAsync([new(other, false)], timeout.Token).ConfigureAwait(true);
                using IDisposable readTwo = await fixture.Service.Operations.AcquireAsync([new(other, false)], timeout.Token).ConfigureAwait(true);
                await File.WriteAllTextAsync(path, "raced", timeout.Token).ConfigureAwait(true);
            }
            Assert.Equal(EOutcome.NotExecuted, (await pending.ConfigureAwait(true)).Outcome);
            Assert.Equal("raced", await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true));
        }
        finally
        {
            if (pending is not null)
            {
                await new TestTaskLifetime(pending, timeout).DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>許可ルート内のlinkから外部へ抜ける操作を拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task SymlinkCannotEscapeAllowedRootAsync()
    {
        using FileTestDirectory fixture = new();
        using FileTestDirectory outside = new();
        string link = Path.Combine(fixture.Root, "link");
        await FileTestDirectory.CreateDirectoryLinkAsync(link, outside.Root).ConfigureAwait(true);
        string path = Path.Combine(link, "blocked");
        NodeResult result = await fixture.Action("file.create").ExecuteAsync(FileTestDirectory.Context(new { path }, NodeValue.FromObject("unsafe"))).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.False(File.Exists(Path.Combine(outside.Root, "blocked")));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Service.AccessPolicy.ResolvePath(fixture.Root));
        Directory.Delete(link);
    }

    /// <summary>宛先を変更した後の内部copy失敗を明示Restore方針で復元します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task InternalFailureRestoresDirectoryAfterDestinationWasReplacedAsync()
    {
        using FileTestDirectory fixture = new(failure: EFileInternalFailurePolicy.Restore);
        string path = Path.Combine(fixture.Root, "source");
        string destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(destination);
        string lockedPath = Path.Combine(path, "locked");
        await File.WriteAllTextAsync(lockedPath, "new").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(destination, "original"), "before").ConfigureAwait(true);
        using FileStream locked = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        NodeResult result = await fixture.Action("directory.copy").ExecuteAsync(FileTestDirectory.Context(new { path, destination, overwrite = true, recursive = true })).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(destination, "original")).ConfigureAwait(true));
        Assert.False(File.Exists(Path.Combine(destination, "locked")));
    }

    /// <summary>サービス再作成後の明示補償で永続退避を照合します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CompensationCanReloadItsVerifiedManifestAsync()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "value");
        await File.WriteAllTextAsync(path, "before").ConfigureAwait(true);
        ICompensableFileNode original = (ICompensableFileNode)fixture.Action("file.upsert");
        NodeExecutionContext context = FileTestDirectory.Context(new { path }, NodeValue.FromObject("after"));
        FileCompensationToken token = await original.CaptureAsync(context).ConfigureAwait(true);
        await original.ExecuteAsync(context).ConfigureAwait(true);
        FileTransferService resumed = new(fixture.Options);
        string type = "file.upsert";
        ICompensableFileNode recovered = (ICompensableFileNode)FileSystemNodeRegistration.Create(fixture.Options, transferService: resumed).Single(node => string.Equals(node.Descriptor.Type, type, StringComparison.Ordinal));
        await recovered.RestoreAsync(token).ConfigureAwait(true);
        Assert.Equal("before", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        await recovered.ReleaseAsync(token).ConfigureAwait(true);
        await recovered.ReleaseAsync(token).ConfigureAwait(true);
    }

    /// <summary>破損した退避を検出した場合は現在の対象を変更しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CorruptCompensationSnapshotFailsBeforeTouchingCurrentDataAsync()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "value");
        await File.WriteAllTextAsync(path, "before").ConfigureAwait(true);
        ICompensableFileNode node = (ICompensableFileNode)fixture.Action("file.upsert");
        NodeExecutionContext context = FileTestDirectory.Context(new { path }, NodeValue.FromObject("after"));
        FileCompensationToken token = await node.CaptureAsync(context).ConfigureAwait(true);
        await node.ExecuteAsync(context).ConfigureAwait(true);
        string stored = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReArtWord.Contents.BackgroundService", "file-recovery", token.Id.ToString("N"), "0");
        await File.WriteAllTextAsync(stored, "corrupt").ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => node.RestoreAsync(token)).ConfigureAwait(true);
        Assert.Equal("after", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        await node.ReleaseAsync(token).ConfigureAwait(true);
    }
}
