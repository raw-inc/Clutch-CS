using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>C#のバイナリ結果を、別CoreのC#入力とファイル書込みへ実配送します。</summary>
public sealed class CSharpBinaryProcessTests
{
    /// <summary>旧文字列解析も含め、二つのworkerとPeer間通信でバイト列を維持します。</summary>
    /// <returns>保存時検証・遠隔実行・ディスク上の実バイト列の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptBytesCrossPeersAndReachFileUnchangedAsync(bool legacy)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token);
        await second.StartAsync(timeout.Token);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
            && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, timeout.Token);
        string producerCode = "public static Task<byte[]> Run() => Task.FromResult(new byte[] { 0, 127, 128, 255, 10, 13 });";
        string consumerCode = "public static byte[] Run(byte[] data) => data.Reverse().ToArray();";
        ScriptAnalysis producer = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", producerCode), timeout.Token);
        ScriptAnalysis consumer = await second.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", consumerCode), timeout.Token);
        Assert.Empty(producer.Diagnostics);
        Assert.Empty(consumer.Diagnostics);
        Assert.Equal("binary", producer.Contract!.Result.Type);
        if (legacy)
        {
            producer = producer with { Contract = new(new Dictionary<string, ValueTypeDefinition>(), new("string")), ValueProtocolVersion = 0 };
            consumer = consumer with { Contract = new(new Dictionary<string, ValueTypeDefinition> { ["data"] = new("string") }, new("string")), ValueProtocolVersion = 0 };
        }
        string file = Path.Combine(second.DirectoryPath, "content", "binary", "script.dat");
        WorkflowDefinition workflow = new("script-binary", "Script binary", [
            new("producer", "script.csharp", JsonSerializer.SerializeToElement(new { code = producerCode, analysis = producer, inputMode = "individual" }, ContractJson.Options), Target: new(EExecutionScope.One, ["a"])),
            new("consumer", "script.csharp", JsonSerializer.SerializeToElement(new { code = consumerCode, analysis = consumer, inputMode = "individual" }, ContractJson.Options), Target: new(EExecutionScope.One, ["b"])),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = file, failurePolicy = "rollbackAll" }), Target: new(EExecutionScope.One, ["b"]))],
            [new("producer", "value", "consumer", ScriptInputPorts.Name("data")), new("consumer", "value", "write", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), timeout.Token);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, workflow.Id, saved.Revision), timeout.Token);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(false);
            return !run.Running;
        }, timeout.Token);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run));
        Assert.Equal(new byte[] { 13, 10, 255, 128, 127, 0 }, await File.ReadAllBytesAsync(file, timeout.Token));
        Assert.All(run.Report!.Nodes.Where(node => node.NodeId is "producer" or "consumer"), node =>
            Assert.Equal(EValueKind.Binary, Assert.Single(node.Targets).Result.Outputs["value"].Kind));
    }
}
