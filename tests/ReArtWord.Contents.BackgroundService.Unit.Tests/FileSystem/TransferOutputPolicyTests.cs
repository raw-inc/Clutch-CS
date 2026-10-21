using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>転送の事前・確定時に同じ保存方針を適用し、既存先の情報を生成済みと偽りません。</summary>
public sealed class TransferOutputPolicyTests
{
    /// <summary>親がない転送先を作り、既定で置換し、スキップ時は保全します。</summary>
    /// <returns>実転送と正常スキップの内容です。</returns>
    [Fact]
    public async Task TransferNodeCreatesParentsAndPreservesSkippedDestinationAsync()
    {
        using FileTestDirectory files = new();
        string sourcePath = Path.Combine(files.Root, "source"), path = Path.Combine(files.Root, "new", "nested", "result");
        await File.WriteAllTextAsync(sourcePath, "first").ConfigureAwait(true);
        object settings = new { sourcePeer = "peer", sourcePath, path };
        Assert.Equal(EOutcome.Succeeded, (await files.Action("file.transfer").ExecuteAsync(FileTestDirectory.Context(settings)).ConfigureAwait(true)).Outcome);
        Assert.Equal("first", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        await File.WriteAllTextAsync(sourcePath, "next").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await files.Action("file.transfer").ExecuteAsync(FileTestDirectory.Context(settings)).ConfigureAwait(true)).Outcome);
        Assert.Equal("next", await File.ReadAllTextAsync(path).ConfigureAwait(true));
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Runtime.Nodes.NodeExecutionContext skip = FileTestDirectory.Context(new { sourcePeer = "peer", sourcePath, path, lockedFileMode = "skip" });
            ICompensableFileNode node = (ICompensableFileNode)files.Action("file.transfer");
            FileCompensationToken token = await node.CaptureAsync(skip).ConfigureAwait(true);
            Contracts.Nodes.NodeResult result = await node.ExecuteAsync(skip).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.True(result.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
            await node.RestoreAsync(token).ConfigureAwait(true);
            await node.ReleaseAsync(token).ConfigureAwait(true);
        }
        Assert.Equal("next", await File.ReadAllTextAsync(path).ConfigureAwait(true));
    }

    /// <summary>転送後に保存先が作成または占有されても、既存先を変更せず確定をスキップします。</summary>
    /// <returns>実転送のハッシュを既存先のハッシュとして返さない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDestinationIsSkippedWithoutPublishingTransferredHashAsync(bool locked)
    {
        using FileTestDirectory files = new();
        string path = Path.Combine(files.Root, "output");
        byte[] content = "new"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content));
        string manifest = FileTransferManifest.ComputeSha256([new(string.Empty, false, content.Length, hash)]);
        FileStageToken stage = await files.Service.OpenStageAsync(new("test", "source", "/source", path, manifest, false,
            Overwrite: true, SkipExisting: !locked, SkipLocked: locked)).ConfigureAwait(true);
        await files.Service.WriteChunkAsync(new(stage, string.Empty, 0, content, hash, content.Length, hash)).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, "previous").ConfigureAwait(true);
        FileStream? owner = null;
        if (locked)
        {
            owner = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        using (owner)
        {
            FileStageCommitResult result = await files.Service.CommitStageAsync(stage).ConfigureAwait(true);
            Assert.True(result.Skipped);
            Assert.Empty(result.ManifestSha256);
            Assert.Equal(0, result.Bytes);
        }
        await files.Service.DiscardStageAsync(stage).ConfigureAwait(true);
        Assert.Equal("previous", await File.ReadAllTextAsync(path).ConfigureAwait(true));
    }
}
