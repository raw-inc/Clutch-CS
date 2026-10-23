using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>転送エラー、中断後の復旧、不要ファイルの整理を検証します。</summary>
public sealed class ArchiveRecoveryTests
{
    /// <summary>再送内容の変更、転送の欠落、ハッシュの不一致を検出することを確認します。</summary>
    /// <returns>転送データの検証。</returns>
    [Fact]
    public async Task UploadRejectsChangedRetriesGapsAndMismatchedHashes()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ArchiveWorkspace workspace = new(Path.Combine(fixture.Root, "transfer"));
        Guid id = Guid.NewGuid();
        byte[] original = [1, 2, 3, 4];
        ArchiveTransfer transfer = new(id, original.Length, Convert.ToHexString(SHA256.HashData(original)));
        Assert.Equal(0, await workspace.BeginAsync(transfer));
        ArchiveChunk first = new(id, 0, Convert.ToBase64String(original[..2]));
        Assert.Equal(2, await workspace.AppendAsync(first));
        Assert.Equal(2, await workspace.AppendAsync(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.AppendAsync(new(id, 0, Convert.ToBase64String(new byte[] { 9, 2 }))));
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.AppendAsync(new(id, 3, Convert.ToBase64String(new byte[] { 4 }))));
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.VerifyAsync(id));
        await workspace.AppendAsync(new(id, 2, Convert.ToBase64String(new byte[] { 3, 5 })));
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.VerifyAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.BeginAsync(transfer with { Sha256 = new string('A', 64) }));
        Assert.Equal(new byte[] { 1, 2, 3, 5 }, await File.ReadAllBytesAsync(workspace.FileFor(id, "archive.zip")));
    }

    /// <summary>中断した復元を再実行せず、その操作による編集保護だけを解除できることを確認します。</summary>
    /// <returns>設定が変わらず、編集保護が解除されることの検証。</returns>
    [Fact]
    public async Task InterruptedProtectionCanBeReleasedWithoutReplayingImport()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync();
        await fixture.Workflows.SaveAsync(new(Guid.NewGuid(), ArchiveTestEnvironment.Definition("preserved", 1), null));
        ClusterArchiveState state = new(fixture.Cluster, fixture.Workflows, fixture.Common);
        ClusterArchiveConfiguration before = await state.CaptureAsync();
        ClusterArchiveConfiguration incoming = before with { Workflows = [] };
        Guid id = Guid.NewGuid();
        ArchiveImportPreview preview = new(id, "original-confirmation", EArchiveImportMode.Replace, "source", "one", DateTimeOffset.UtcNow, [], [], ["preserved"], 0);
        ArchiveImportPlan plan = new(preview, new(id, 1, new string('A', 64)), before, incoming, fixture.Cluster.GetPeers().ToArray());
        ArchiveWorkspace workspace = new(Path.Combine(fixture.Root, "cluster-archives"));
        ArchiveImportRequest request = new(id, preview.Confirmation);
        await workspace.WriteAsync(id, "plan.json", plan);
        await workspace.WriteAsync(id, "import-request.json", request);
        workspace.Claim(id);
        SnapshotEditProtection protection = new(fixture.Repository, fixture.Cluster);
        Assert.Equal(EOutcome.Succeeded, (await protection.AcquireAsync(id, [new(id, "preserved", null), new(id, ClusterConfigurationStore.Id, null, true)])).Outcome);
        await workspace.WriteAsync(id, "result.json", new ArchiveOperationResult(id, "protecting", false, EOutcome.Unknown));

        ArchiveOperationResult interrupted = (await fixture.Archives.ReadAsync(id))!;
        Assert.True(interrupted.Completed);
        Assert.Equal(EOutcome.Unknown, interrupted.Outcome);
        Assert.Equal(EOutcome.Unknown, (await fixture.Archives.ImportAsync(request)).Outcome);
        Assert.NotEmpty(await protection.ReadAsync());
        ArchiveOperationResult recovered = await fixture.Archives.ReconcileAsync(id);
        Assert.Equal(EOutcome.NotExecuted, recovered.Outcome);
        Assert.Empty(await protection.ReadAsync());
        Assert.NotNull(await fixture.Workflows.GetAsync("preserved"));
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Archives.ImportAsync(request)).Outcome);
        Assert.Empty(fixture.Errors);
    }

    /// <summary>古い作業ファイルを削除し、結果不明や使用中のファイルと操作記録は残すことを確認します。</summary>
    /// <returns>24時間経過後に残るファイルの検証。</returns>
    [Fact]
    public async Task RetentionKeepsUnknownOperationsAndRecentDownloads()
    {
        ArchiveTestEnvironment fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ArchiveWorkspace workspace = new(Path.Combine(fixture.Root, "retention"));
        Guid completed = Guid.NewGuid(), unknown = Guid.NewGuid(), active = Guid.NewGuid(), abandoned = Guid.NewGuid();
        foreach (Guid id in new[] { completed, unknown, active, abandoned })
        {
            ArchiveTransfer transfer = new(id, 1, Convert.ToHexString(SHA256.HashData(new byte[] { 1 })));
            await workspace.BeginAsync(transfer);
            await workspace.AppendAsync(new(id, 0, "AQ=="));
            if (id != abandoned)
            {
                EOutcome outcome = EOutcome.Succeeded;
                if (id == unknown)
                {
                    outcome = EOutcome.Unknown;
                }
                await workspace.WriteAsync(id, "result.json", new ArchiveOperationResult(id, "completed", true, outcome, File: transfer));
                workspace.Claim(id);
            }
            foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(workspace.FileFor(id, "archive.zip"))!))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            }
        }
        await workspace.ReadChunkAsync(new(active, 0));
        await workspace.CleanAsync();
        Assert.False(File.Exists(workspace.FileFor(completed, "archive.zip")));
        Assert.False(File.Exists(workspace.FileFor(abandoned, "archive.zip")));
        Assert.True(File.Exists(workspace.FileFor(unknown, "archive.zip")));
        Assert.True(File.Exists(workspace.FileFor(active, "archive.zip")));
        Assert.NotNull(await workspace.ReadAsync<ArchiveOperationResult>(completed, "result.json"));
        Assert.True(File.Exists(workspace.FileFor(completed, "started")));
    }
}
