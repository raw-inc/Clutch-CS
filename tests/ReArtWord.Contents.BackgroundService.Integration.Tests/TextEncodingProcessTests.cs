using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実ファイルの復号と再符号化を別Coreへ渡し、書込みまで確認します。</summary>
public sealed class TextEncodingProcessTests
{
    /// <summary>正常内容は指定した符号へ変換し、不正内容は後続ファイルを作りません。</summary>
    /// <returns>二つのCoreと実通信・ファイル操作の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsDecodesAndWritesOnAnotherPeerAsync(bool invalid)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string source = Path.Combine(first.DirectoryPath, "content", "source.txt");
        string destination = Path.Combine(second.DirectoryPath, "content", "shift-jis.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        byte[] content = Convert.FromHexString("FEFF65E5672C8A9E000D000A");
        if (invalid)
        {
            content = [0xC0, 0x80];
        }
        await File.WriteAllBytesAsync(source, content).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        WorkflowDefinition definition = new("text-file", "Decode and encode file", [
            new("read", "file.read", JsonSerializer.SerializeToElement(new { path = source }), Target: new(EExecutionScope.One, ["a"])),
            new("decode", "text.decode", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["b"])),
            new("encode", "text.encode", JsonSerializer.SerializeToElement(new { encoding = "shift_jis", bom = "none" }), Target: new(EExecutionScope.One, ["b"])),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = destination }), Target: new(EExecutionScope.One, ["b"])),
        ], [new("read", "value", "decode", "value"), new("decode", "value", "encode", "value"), new("encode", "value", "write", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        if (invalid)
        {
            Assert.Equal(EOutcome.Failed, run.Outcome);
            Assert.False(File.Exists(destination));
            return;
        }
        Assert.True(run.Outcome is EOutcome.Succeeded, JsonSerializer.Serialize(run));
        TargetNodeResult decoded = Assert.Single(run.Report!.Nodes.Single(node => node.NodeId is "decode").Targets);
        Assert.Equal("b", decoded.PeerId);
        Assert.Equal("日本語\r\n", decoded.Result.Outputs["value"].GetString());
        Assert.Equal(Convert.FromHexString("93FA967B8CEA0D0A"), await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
        Assert.Equal(content, await File.ReadAllBytesAsync(source).ConfigureAwait(true));
    }
}
