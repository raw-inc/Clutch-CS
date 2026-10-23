using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>転送の改変、中断した保護、資材整理が設定の再実行へつながらないことを検証します。</summary>
public sealed class ArchiveRecoveryTests
{
    /// <summary>同じ断片の再送だけを認め、欠落や改変を完成扱いにしません。</summary>
    /// <returns>転送全体の照合結果です。</returns>
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

    /// <summary>開始済みの元操作は再受付で復元せず、元の保護だけを解放します。</summary>
    /// <returns>設定未変更と保護解除を区別した回復結果です。</returns>
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

    /// <summary>成功した古い資材だけを回収し、未確認・活動中の資材と元操作の記録を残します。</summary>
    /// <returns>24時間経過後の保存境界です。</returns>
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
