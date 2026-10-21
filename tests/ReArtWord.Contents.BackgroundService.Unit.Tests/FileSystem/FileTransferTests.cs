using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>32KiB転送片の照合、再要求、tree確定を実体で検証します。</summary>
public sealed class FileTransferTests
{
    /// <summary>途中段階をサービス再作成後も照合し、完成前には宛先を作りません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task InterruptedStageResumesOnlyMatchingChunksAndCommitsVerifiedContentAsync()
    {
        using FileTestDirectory fixture = new();
        byte[] bytes = Enumerable.Range(0, 100000).Select(number => (byte)(number % 251)).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string destination = Path.Combine(fixture.Root, "received.bin");
        string manifest = FileTransferManifest.ComputeSha256([new(string.Empty, false, bytes.Length, hash)]);
        FileStageRequest request = new("request", "source-peer", "/source/file", destination, manifest, false);
        FileStageToken stage = await fixture.Service.OpenStageAsync(request).ConfigureAwait(true);
        FileChunkWriteRequest first = Chunk(stage, bytes, 0, 32768, hash);
        FileChunkWriteRequest transferred = JsonSerializer.Deserialize<FileChunkWriteRequest>(JsonSerializer.Serialize(first))!;
        await fixture.Service.WriteChunkAsync(transferred).ConfigureAwait(true);
        string metadata = Path.Combine(Directory.GetDirectories(fixture.Root, ".contents-transfer-*").Single(), "state.json");
        DateTime stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(metadata, stamp);
        await fixture.Service.WriteChunkAsync(first).ConfigureAwait(true);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(metadata));
        Assert.False(File.Exists(destination));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CommitStageAsync(stage)).ConfigureAwait(true);
        FileTransferService resumed = new(fixture.Options);
        FileStageToken reopened = await resumed.OpenStageAsync(request).ConfigureAwait(true);
        Assert.Equal(stage, reopened);
        await resumed.WriteChunkAsync(first).ConfigureAwait(true);
        byte[] wrong = first.Data.ToArray();
        wrong[0] ^= 1;
        FileChunkWriteRequest changed = first with { Data = wrong, ChunkSha256 = Convert.ToHexString(SHA256.HashData(wrong)) };
        await Assert.ThrowsAsync<IOException>(() => resumed.WriteChunkAsync(changed)).ConfigureAwait(true);
        for (int offset = 32768; offset < bytes.Length; offset += 32768)
        {
            await resumed.WriteChunkAsync(Chunk(stage, bytes, offset, Math.Min(32768, bytes.Length - offset), hash)).ConfigureAwait(true);
        }
        FileStageCommitResult result = await resumed.CommitStageAsync(stage).ConfigureAwait(true);
        Assert.Equal(bytes.Length, result.Bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
        Assert.Equal(result, await resumed.CommitStageAsync(stage).ConfigureAwait(true));
        await resumed.DiscardStageAsync(stage).ConfigureAwait(true);
        Assert.True(File.Exists(destination));
    }

    /// <summary>ディレクトリと空ディレクトリもマニフェストで転送します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task RecursiveTransferPreservesTreeAndExplicitRepeatIsIdempotentAsync()
    {
        using FileTestDirectory fixture = new();
        string sourcePath = Path.Combine(fixture.Root, "source");
        string path = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(Path.Combine(sourcePath, "empty"));
        Directory.CreateDirectory(Path.Combine(sourcePath, "nested"));
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "nested", "value"), "payload").ConfigureAwait(true);
        object settings = new { sourcePeer = "peer", sourcePath, path, requestId = "tree", recursive = true };
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.transfer").ExecuteAsync(FileTestDirectory.Context(settings)).ConfigureAwait(true)).Outcome);
        Assert.True(Directory.Exists(Path.Combine(path, "empty")));
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(path, "nested", "value")).ConfigureAwait(true));
        Assert.Equal(EOutcome.Succeeded, (await fixture.Action("file.transfer").ExecuteAsync(FileTestDirectory.Context(settings)).ConfigureAwait(true)).Outcome);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "nested", "value"), "changed").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, (await fixture.Action("file.transfer").ExecuteAsync(FileTestDirectory.Context(settings)).ConfigureAwait(true)).Outcome);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(path, "nested", "value")).ConfigureAwait(true));
    }

    /// <summary>越境相対パスと想定外の段階実体を確定させません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StageRejectsTraversalAndUnspecifiedTreeEntriesAsync()
    {
        using FileTestDirectory fixture = new();
        string destination = Path.Combine(fixture.Root, "tree");
        string manifest = FileTransferManifest.ComputeSha256([new(string.Empty, true, 0, string.Empty)]);
        FileStageToken stage = await fixture.Service.OpenStageAsync(new("request", "source", "/tree", destination, manifest, true)).ConfigureAwait(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.StageDirectoryAsync(new(stage, "../escaped"))).ConfigureAwait(true);
        string staging = Directory.GetDirectories(fixture.Root, ".contents-transfer-*").Single();
        await File.WriteAllTextAsync(Path.Combine(staging, "content", "unexpected"), "tampered").ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CommitStageAsync(stage)).ConfigureAwait(true);
        Assert.False(Directory.Exists(destination));
    }

    /// <summary>指定範囲から独立した転送片を作成します。</summary>
    /// <returns>digestを含む転送片です。</returns>
    private static FileChunkWriteRequest Chunk(FileStageToken stage, byte[] source, int offset, int count, string hash)
    {
        byte[] bytes = source.AsSpan(offset, count).ToArray();
        return new(stage, string.Empty, offset, bytes, Convert.ToHexString(SHA256.HashData(bytes)), source.Length, hash);
    }
}
