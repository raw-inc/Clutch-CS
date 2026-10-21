using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Nodes.AI;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FilesTransactions;

/// <summary>AI/mediaのファイル範囲と生成物の実体同一性を検証します。</summary>
public sealed class FileArtifactTests
{
    /// <summary>明示したスキップ参照は同Peerの既存パスとして扱い、通常の生成物からハッシュ検証を外しません。</summary>
    /// <returns>保証の違いを区別する参照検証です。</returns>
    [Fact]
    public async Task SkippedOutputRemainsAnOwnedExistingPathAsync()
    {
        using Media.MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "existing.bin");
        await File.WriteAllTextAsync(path, "existing").ConfigureAwait(true);
        FileAccessPolicy policy = new(files.Options.Files);
        NodeValue skipped = NodeValue.FromObject(new { peerId = "peer", path, skipped = true, skipReason = "locked" });
        Assert.Equal(path, await FileArtifactAccess.ResolveInputAsync("peer", skipped, policy).ConfigureAwait(true));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileArtifactAccess.ResolveInputAsync("other-peer", skipped, policy)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileArtifactAccess.ResolveInputAsync("peer", NodeValue.FromObject(new { peerId = "peer", path }), policy)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileArtifactAccess.ResolveInputAsync("peer", NodeValue.FromObject(new { peerId = "peer", path, skipped = false, skipReason = "locked" }), policy)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileArtifactAccess.ResolveInputAsync("peer", NodeValue.FromObject(new { peerId = "peer", path, skipped = true, skipReason = "existing", sha256 = "incorrect" }), policy)).ConfigureAwait(true);
    }

    /// <summary>同名パスでも別Peerやハッシュ不一致の生成物は予約対象にしません。</summary>
    /// <returns>実ファイル検証完了。</returns>
    [Fact]
    public async Task MediaAndAiExposeValidatedFileFootprints()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string input = Path.Combine(root, "input.png");
        string output = Path.Combine(root, "output.png");
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(input, bytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        MediaOptions media = new("ffmpeg", "ffprobe", new FileSystemOptions([root]));
        IFileFootprintNode converter = (IFileFootprintNode)MediaNodeRegistration.Create(media)[0];
        NodeValue artifact = NodeValue.FromObject(new { peerId = "one", path = input, sha256 = hash });
        NodeExecutionContext context = Context(output, artifact);
        IReadOnlyList<Contracts.Files.Transactions.FilePathAccess> paths = await converter.GetFileAccessAsync(context);
        Assert.Contains(paths, path => !path.Write && string.Equals(path.Path, input, StringComparison.Ordinal));
        Assert.Contains(paths, path => path.Write && string.Equals(path.Path, output, StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await converter.GetFileAccessAsync(Context(output, NodeValue.FromObject(new { peerId = "other", path = input, sha256 = hash }))).ConfigureAwait(false));
        await File.WriteAllTextAsync(input, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await converter.GetFileAccessAsync(context).ConfigureAwait(false));
        SecretStore secrets = new(new SqliteRepository(Path.Combine(root, "state.db")));
        IFileFootprintNode generator = (IFileFootprintNode)AiNodeRegistration.Create(secrets, new AiOptions(), media).Single(node => string.Equals(node.Descriptor.Type, "ai.picture.generate", StringComparison.Ordinal));
        Assert.Equal(output, Assert.Single(await generator.GetFileAccessAsync(Context(output, NodeValue.FromObject("prompt")))).Path);
        IFileFootprintNode classifier = (IFileFootprintNode)AiNodeRegistration.Create(secrets, new AiOptions(), media).Single(node => string.Equals(node.Descriptor.Type, "ai.content.route", StringComparison.Ordinal));
        NodeExecutionContext video = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "one", JsonSerializer.SerializeToElement(new { mediaKind = "video" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(input) }, TimeProvider.System, TimeZoneInfo.Utc);
        Assert.Contains(await classifier.GetFileAccessAsync(video), path => path.Write && path.DirectoryEntriesOnly && string.Equals(path.Path, root, StringComparison.Ordinal));
    }

    /// <summary>読取りsourceの内側に退避を作ってコピー内容を変えません。</summary>
    /// <returns>退避前拒否の確認。</returns>
    [Fact]
    public async Task BackupInsideCopySourceIsRejectedBeforeChangingSource()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        string recovery = Path.Combine(source, "recovery");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "value.txt"), "original");
        ICompensableFileNode copy = (ICompensableFileNode)FileSystemNodeRegistration.Create(new FileSystemOptions([root], RecoveryDirectory: recovery)).Single(node => string.Equals(node.Descriptor.Type, "directory.copy", StringComparison.Ordinal));
        NodeExecutionContext context = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "copy", "one", JsonSerializer.SerializeToElement(new { path = source, destination, recursive = true }), new Dictionary<string, NodeValue>(StringComparer.Ordinal), TimeProvider.System, TimeZoneInfo.Utc);
        await Assert.ThrowsAsync<IOException>(async () => await copy.CaptureAsync(context).ConfigureAwait(false));
        Assert.False(Directory.Exists(recovery));
        Assert.False(Directory.Exists(destination));
    }

    /// <summary>ファイル範囲だけを検証する設定です。</summary>
    /// <returns>媒体処理の文脈。</returns>
    private static NodeExecutionContext Context(string outputPath, NodeValue value) => new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", "node", "one", JsonSerializer.SerializeToElement(new { outputPath, format = "png" }), new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = value }, TimeProvider.System, TimeZoneInfo.Utc);
}
