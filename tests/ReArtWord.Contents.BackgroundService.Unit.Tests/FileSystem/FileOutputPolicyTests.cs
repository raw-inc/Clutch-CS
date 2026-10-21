using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.FileSystem;

/// <summary>保存先の親作成・上書き・スキップを、移動元と既存内容の保全まで実体で確認します。</summary>
public sealed class FileOutputPolicyTests
{
    /// <summary>新しい保存先の親を再帰作成し、既定の上書きでも入力内容を保ちます。</summary>
    /// <returns>元ファイルと保存先の実内容です。</returns>
    [Theory]
    [InlineData("file.create")]
    [InlineData("file.upsert")]
    [InlineData("file.copy")]
    [InlineData("file.move")]
    public async Task MissingParentsAreCreatedAndDefaultsOverwriteAsync(string operation)
    {
        using FileTestDirectory files = new();
        string source = Path.Combine(files.Root, "source.txt");
        string output = Path.Combine(files.Root, "new", "nested", "output.txt");
        await File.WriteAllTextAsync(source, "first").ConfigureAwait(true);
        NodeExecutionContext context = Context(operation, source, output);
        Assert.Equal(EOutcome.Succeeded, (await files.Action(operation).ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal("first", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        await File.WriteAllTextAsync(source, "second").ConfigureAwait(true);
        context = Context(operation, source, output, text: "second");
        Assert.Equal(EOutcome.Succeeded, (await files.Action(operation).ExecuteAsync(context).ConfigureAwait(true)).Outcome);
        Assert.Equal("second", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial"));
    }

    /// <summary>スキップでは出力と移動元を変更せず、後続へ既存パスを渡します。</summary>
    /// <returns>操作の種類を跨いだ同じ保存方針です。</returns>
    [Theory]
    [InlineData("file.create")]
    [InlineData("file.replace")]
    [InlineData("file.upsert")]
    [InlineData("file.append")]
    [InlineData("file.copy")]
    [InlineData("file.move")]
    [InlineData("file.rename")]
    public async Task ExistingSkipPreservesOutputAndSourceAsync(string operation)
    {
        using FileTestDirectory files = new();
        string source = Path.Combine(files.Root, "source.txt"), output = Path.Combine(files.Root, "output.txt");
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(true);
        await File.WriteAllTextAsync(output, "previous").ConfigureAwait(true);
        NodeResult result = await files.Action(operation).ExecuteAsync(Context(operation, source, output, "skip")).ConfigureAwait(true);
        Skipped(result, output, "existing");
        Assert.Equal("previous", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        Assert.Equal("source", await File.ReadAllTextAsync(source).ConfigureAwait(true));
    }

    /// <summary>既存出力のロックを強制解除せず、keepとrestore双方で指定どおりに扱います。</summary>
    /// <returns>スキップ時の正常終了と、既定エラーの非実行です。</returns>
    [Theory]
    [InlineData("file.upsert", false)]
    [InlineData("file.upsert", true)]
    [InlineData("file.copy", false)]
    [InlineData("file.move", true)]
    public async Task LockedOutputIsNotReadForSkippedBackupAsync(string operation, bool restore)
    {
        using FileTestDirectory files = new(failure: restore ? EFileInternalFailurePolicy.Restore
                                                             : EFileInternalFailurePolicy.Keep);
        string source = Path.Combine(files.Root, "source.txt"), output = Path.Combine(files.Root, "output.txt");
        await File.WriteAllTextAsync(source, "source").ConfigureAwait(true);
        await File.WriteAllTextAsync(output, "previous").ConfigureAwait(true);
        using (FileStream locked = new(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            NodeResult skipped = await files.Action(operation).ExecuteAsync(Context(operation, source, output, locked: "skip")).ConfigureAwait(true);
            Skipped(skipped, output, "locked");
            NodeResult failed = await files.Action(operation).ExecuteAsync(Context(operation, source, output)).ConfigureAwait(true);
            Assert.Equal(EOutcome.NotExecuted, failed.Outcome);
            Assert.Equal("file.outputLocked", failed.ReasonCode);
        }
        Assert.Equal("previous", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        Assert.Equal("source", await File.ReadAllTextAsync(source).ConfigureAwait(true));
    }

    /// <summary>全対象補償の退避時にスキップを確定し、補償も既存先へ触りません。</summary>
    /// <returns>ロック保持中でも完了するcapture・execute・restore・releaseです。</returns>
    [Fact]
    public async Task SkippedPreparedTargetIsNeverRestoredOverExistingContentAsync()
    {
        using FileTestDirectory files = new();
        string output = Path.Combine(files.Root, "output.txt");
        await File.WriteAllTextAsync(output, "previous").ConfigureAwait(true);
        NodeExecutionContext context = Context("file.upsert", output, output, locked: "skip");
        ICompensableFileNode node = (ICompensableFileNode)files.Action("file.upsert");
        using (FileStream locked = new(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            FileCompensationToken token = await node.CaptureAsync(context).ConfigureAwait(true);
            Skipped(await node.ExecuteAsync(context).ConfigureAwait(true), output, "locked");
            await node.RestoreAsync(token).ConfigureAwait(true);
            await node.ReleaseAsync(token).ConfigureAwait(true);
        }
        Assert.Equal("previous", await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>treeの保存先を再帰作成し、スキップ時には移動元も既存treeも維持します。</summary>
    /// <returns>directory操作の保存方針です。</returns>
    [Theory]
    [InlineData("directory.copy")]
    [InlineData("directory.move")]
    public async Task DirectoryOutputsCreateParentsAndSkipWholeTreeAsync(string operation)
    {
        using FileTestDirectory files = new();
        string source = Path.Combine(files.Root, "source"), output = Path.Combine(files.Root, "nested", "output");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file"), "source").ConfigureAwait(true);
        NodeExecutionContext initial = FileTestDirectory.Context(new { path = source, destination = output, recursive = true });
        Assert.Equal(EOutcome.Succeeded, (await files.Action(operation).ExecuteAsync(initial).ConfigureAwait(true)).Outcome);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file"), "new").ConfigureAwait(true);
        NodeExecutionContext skipped = FileTestDirectory.Context(new { path = source, destination = output, recursive = true, existingFileMode = "skip" });
        Skipped(await files.Action(operation).ExecuteAsync(skipped).ConfigureAwait(true), output, "existing");
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(output, "file")).ConfigureAwait(true));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(source, "file")).ConfigureAwait(true));
    }

    /// <summary>退避後に外部が作った既存先をスキップした場合も、補償で削除しません。</summary>
    /// <returns>遅れて現れた保存先の保全です。</returns>
    [Fact]
    public async Task DestinationCreatedAfterCaptureIsExcludedFromCompensationAsync()
    {
        using FileTestDirectory files = new();
        string output = Path.Combine(files.Root, "output");
        NodeExecutionContext context = Context("file.upsert", output, output, "skip");
        ICompensableFileNode node = (ICompensableFileNode)files.Action("file.upsert");
        FileCompensationToken token = await node.CaptureAsync(context).ConfigureAwait(true);
        await File.WriteAllTextAsync(output, "external").ConfigureAwait(true);
        Skipped(await node.ExecuteAsync(context).ConfigureAwait(true), output, "existing");
        await node.RestoreAsync(token).ConfigureAwait(true);
        await node.ReleaseAsync(token).ConfigureAwait(true);
        Assert.Equal("external", await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>保存済みのoverwrite=falseは上書き禁止を維持し、新しい明示指定で変更できます。</summary>
    /// <returns>旧設定と新設定の優先順位です。</returns>
    [Fact]
    public async Task ExplicitLegacyOverwriteFalseRemainsProtectedAsync()
    {
        using FileTestDirectory files = new();
        string source = Path.Combine(files.Root, "source"), output = Path.Combine(files.Root, "output");
        await File.WriteAllTextAsync(source, "new").ConfigureAwait(true);
        await File.WriteAllTextAsync(output, "previous").ConfigureAwait(true);
        NodeExecutionContext legacy = FileTestDirectory.Context(new { path = source, destination = output, overwrite = false });
        Assert.Equal(EOutcome.NotExecuted, (await files.Action("file.copy").ExecuteAsync(legacy).ConfigureAwait(true)).Outcome);
        Assert.Equal("previous", await File.ReadAllTextAsync(output).ConfigureAwait(true));
        NodeExecutionContext current = FileTestDirectory.Context(new { path = source, destination = output, overwrite = false, existingFileMode = "overwrite" });
        Assert.Equal(EOutcome.Succeeded, (await files.Action("file.copy").ExecuteAsync(current).ConfigureAwait(true)).Outcome);
        Assert.Equal("new", await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>操作種類に合わせて同じ保存方針を与えます。</summary>
    /// <returns>独立した実行です。</returns>
    private static NodeExecutionContext Context(string operation, string source, string output, string? exists = null, string? locked = null, string text = "first")
    {
        bool relocate = operation is "file.copy" or "file.move" or "file.rename";
        Dictionary<string, object> settings = new() { ["path"] = relocate ? source
                                                                          : output };
        if (exists is not null)
        {
            settings["existingFileMode"] = exists;
        }
        if (locked is not null)
        {
            settings["lockedFileMode"] = locked;
        }
        if (relocate)
        {
            settings["destination"] = output;
        }
        return FileTestDirectory.Context(settings, NodeValue.FromObject(text));
    }

    /// <summary>未生成の内容を生成済みとせず、既存先の参照だけを返すことを確認します。</summary>
    private static void Skipped(NodeResult result, string output, string reason)
    {
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.True(result.Outputs["value"].Json.GetProperty("skipped").GetBoolean());
        Assert.Equal(reason, result.Outputs["value"].Json.GetProperty("skipReason").GetString());
        Assert.Equal(Path.GetFullPath(output), Path.GetFullPath(result.Outputs["value"].Json.GetProperty("path").GetString()!));
    }
}
