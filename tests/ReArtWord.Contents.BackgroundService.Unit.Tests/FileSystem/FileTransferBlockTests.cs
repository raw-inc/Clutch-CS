using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>大きな分割単位でも部分失敗・内容変更・最終検証の境界を守ります。</summary>
public sealed class FileTransferBlockTests
{
    /// <summary>部分書込みとの重複範囲が同じ場合だけ続行し、異なる場合は前の内容を維持します。</summary>
    /// <returns>再開と最終hashの検証です。</returns>
    [Fact]
    public async Task PartialOverlapCannotOverwritePreviouslyVerifiedBytes()
    {
        using FileTestDirectory fixture = new();
        byte[] bytes = RandomNumberGenerator.GetBytes(FileTransferLimits.StreamBytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string destination = Path.Combine(fixture.Root, "blocks.bin");
        FileStageToken stage = await fixture.Service.OpenStageAsync(new("blocks", "source", "original", destination,
            FileTransferManifest.ComputeSha256([new(string.Empty, false, bytes.Length, hash)]), false)).ConfigureAwait(true);
        await fixture.Service.WriteChunkAsync(Chunk(stage, bytes.AsMemory(0, 16384), bytes.Length, hash)).ConfigureAwait(true);
        byte[] changed = bytes.ToArray();
        changed[0] ^= 1;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.WriteChunkAsync(Chunk(stage, changed, bytes.Length, hash))).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CommitStageAsync(stage)).ConfigureAwait(true);
        Assert.False(File.Exists(destination));
        await fixture.Service.WriteChunkAsync(Chunk(stage, bytes, bytes.Length, hash)).ConfigureAwait(true);
        FileStageCommitResult result = await fixture.Service.CommitStageAsync(stage).ConfigureAwait(true);
        Assert.Equal(bytes.Length, result.Bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
    }

    /// <summary>全体hashの不一致をサイズ一致だけで確定しません。</summary>
    /// <returns>誤った全体digestと容量超過の拒否です。</returns>
    [Fact]
    public async Task RejectsOversizedBlocksAndIncorrectWholeFileHash()
    {
        using FileTestDirectory fixture = new();
        byte[] bytes = RandomNumberGenerator.GetBytes(FileTransferLimits.StreamBytes + 1);
        string wrong = new('0', 64);
        string destination = Path.Combine(fixture.Root, "unverified.bin");
        FileStageToken stage = await fixture.Service.OpenStageAsync(new("wrong", "source", "original", destination,
            FileTransferManifest.ComputeSha256([new(string.Empty, false, bytes.Length, wrong)]), false)).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.WriteChunkAsync(Chunk(stage, bytes, bytes.Length, wrong))).ConfigureAwait(true);
        await fixture.Service.WriteChunkAsync(Chunk(stage, bytes.AsMemory(0, FileTransferLimits.StreamBytes), bytes.Length, wrong)).ConfigureAwait(true);
        await fixture.Service.WriteChunkAsync(Chunk(stage, bytes.AsMemory(FileTransferLimits.StreamBytes), bytes.Length, wrong) with { Offset = FileTransferLimits.StreamBytes }).ConfigureAwait(true);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CommitStageAsync(stage)).ConfigureAwait(true);
        Assert.False(File.Exists(destination));
    }

    /// <summary>大きな読取りでも固定した送信元版の変更を検出します。</summary>
    /// <returns>読み終えた内容と変更後の拒否です。</returns>
    [Fact]
    public async Task StreamReadsRejectAChangedSource()
    {
        using FileTestDirectory fixture = new();
        string path = Path.Combine(fixture.Root, "source.bin");
        byte[] bytes = RandomNumberGenerator.GetBytes(FileTransferLimits.StreamBytes);
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
        FileEntryInfo file = await fixture.Service.StatAsync(path).ConfigureAwait(true);
        FileChunkRequest request = new(path, 0, FileTransferLimits.StreamBytes, file.Length, file.Sha256);
        FileChunk chunk = await fixture.Service.ReadChunkAsync(request).ConfigureAwait(true);
        Assert.Equal(bytes, chunk.Data.ToArray());
        Assert.Equal(file.Sha256, chunk.Sha256);
        await File.WriteAllBytesAsync(path, new byte[bytes.Length]).ConfigureAwait(true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ReadChunkAsync(request)).ConfigureAwait(true);
    }

    /// <summary>全体digestと分割digestを独立して指定します。</summary>
    /// <returns>一つの分割書込みです。</returns>
    private static FileChunkWriteRequest Chunk(FileStageToken stage, ReadOnlyMemory<byte> bytes, long total, string hash)
        => new(stage, string.Empty, 0, bytes, Convert.ToHexString(SHA256.HashData(bytes.Span)), total, hash);
}
