using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>再帰的作成の変更範囲、競合、補償と既存内容の保護を確認します。</summary>
public sealed class RecursiveDirectoryTests
{
    /// <summary>存在しない最上位の親から、作成する階層全体を排他対象にします。</summary>
    /// <returns>実ディレクトリと公開footprintの確認です。</returns>
    [Fact]
    public async Task CreatesParentsWithinTheDeclaredWriteScopeAsync()
    {
        using FileTestDirectory files = new();
        string first = Path.Combine(files.Root, "downloads");
        string path = Path.Combine(first, "group", "audio");
        IActionNode node = files.Action("directory.create");
        NodeExecutionContext context = FileTestDirectory.Context(new { path });
        IReadOnlyList<FilePathAccess> access = await ((IFileFootprintNode)node).GetFileAccessAsync(context).ConfigureAwait(true);
        Assert.Equal(first, Assert.Single(access).Path);
        NodeResult result = await node.ExecuteAsync(context).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(Directory.Exists(path));
        Assert.Equal(path, result.Outputs["value"].Json.GetProperty("path").GetString());
    }

    /// <summary>補償予約中の兄弟作成を拒否し、新しい祖先だけを復元時に取り除きます。</summary>
    /// <returns>元からある親とファイルは保持されます。</returns>
    [Fact]
    public async Task CompensationCoversCreatedAncestorsAndTheirSiblingsAsync()
    {
        using FileTestDirectory files = new();
        string original = Path.Combine(files.Root, "keep.txt");
        await File.WriteAllTextAsync(original, "unchanged").ConfigureAwait(true);
        string first = Path.Combine(files.Root, "created");
        NodeExecutionContext context = FileTestDirectory.Context(new { path = Path.Combine(first, "child", "leaf") });
        ICompensableFileNode node = (ICompensableFileNode)files.Action("directory.create");
        FileCompensationToken token = await node.CaptureAsync(context).ConfigureAwait(true);
        try
        {
            NodeResult conflict = await node.ExecuteAsync(FileTestDirectory.Context(new { path = Path.Combine(first, "sibling") })).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, conflict.Outcome);
            Assert.Equal(EOutcome.Succeeded, (await node.ExecuteAsync(context).ConfigureAwait(true)).Outcome);
            await node.RestoreAsync(token).ConfigureAwait(true);
            Assert.False(Directory.Exists(first));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(original).ConfigureAwait(true));
        }
        finally
        {
            await node.ReleaseAsync(token).ConfigureAwait(true);
        }
    }

    /// <summary>親階層がファイルの場合は置換せず、作成を開始しません。</summary>
    /// <returns>既存ファイルの保持です。</returns>
    [Fact]
    public async Task FileInAncestorIsNeverReplacedAsync()
    {
        using FileTestDirectory files = new();
        string existing = Path.Combine(files.Root, "file");
        await File.WriteAllTextAsync(existing, "keep").ConfigureAwait(true);
        NodeResult result = await files.Action("directory.create").ExecuteAsync(FileTestDirectory.Context(new { path = Path.Combine(existing, "child", "leaf") })).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing).ConfigureAwait(true));
    }
}
