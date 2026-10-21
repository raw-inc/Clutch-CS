using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>パッケージの受付・結果・解析固定を、複数Coreの実通信で確認します。</summary>
public sealed class ScriptPackageProcessTests
{
    /// <summary>既定実行環境はPeerローカルに保持し、空欄パスの共有WFを指定Peerで実行します。</summary>
    /// <returns>二つの実Core、登録API、解析、保存、遠隔実行、再起動後の確認です。</returns>
    [Fact]
    public async Task RuntimeDefaultIsLocalAndSurvivesRestartAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        string nodePath = NodeRuntime();
        ScriptRuntimeCatalog selected = await first.PostAsync<ScriptRuntimeCatalog>("scripts.runtimes.change", new ScriptRuntimeChange("javascript", nodePath, true)).ConfigureAwait(true);
        Assert.Equal(nodePath, selected.DefaultPath);
        Assert.NotNull(selected.InstallDirectory);
        Assert.Null(selected.InstallProblem);
        Assert.Null((await second.GetAsync<ScriptRuntimeCatalog>("scripts.runtimes.list", new ScriptRuntimeQuery("javascript")).ConfigureAwait(true)).DefaultPath);
        string code = "export function run() { return 'remote'; }";
        ScriptAnalysis analysis = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("javascript", code)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        NodeDefinition script = new("script", "script.javascript", JsonSerializer.SerializeToElement(new { code, analysis }, ContractJson.Options), Target: new(EExecutionScope.One, ["a"]));
        WorkflowDefinition workflow = new("default-runtime-script", "Default runtime", [script], []);
        Assert.Equal(EOutcome.Succeeded, (await second.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null)).ConfigureAwait(true)).Activation.Outcome);
        await RunAsync(second, workflow.Id).ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        Assert.Equal(selected.Revision, (await first.GetAsync<ScriptRuntimeCatalog>("scripts.runtimes.list", new ScriptRuntimeQuery("javascript")).ConfigureAwait(true)).Revision);
        await RunAsync(second, workflow.Id).ConfigureAwait(true);
    }

    /// <summary>構成を持たないPeerでも保存・共有でき、実行は導入済みの明示Peerで行います。</summary>
    /// <returns>Peerローカル構成と共有WF・再起動後の実行確認です。</returns>
    [Theory]
    [InlineData("csharp", EScriptPackageKind.Nuget, false)]
    [InlineData("javascript", EScriptPackageKind.Npm, false)]
    [InlineData("csharp", EScriptPackageKind.Nuget, true)]
    [InlineData("javascript", EScriptPackageKind.Npm, true)]
    public async Task PackageConfigurationStaysLocalAndWorkflowRunsOnItsSelectedPeerAsync(string language, EScriptPackageKind kind, bool individual)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string marker = Path.Combine(first.DirectoryPath, "node-options-must-not-run.txt");
        if (kind == EScriptPackageKind.Npm)
        {
            string preload = Path.Combine(first.DirectoryPath, "preload.mjs");
            await File.WriteAllTextAsync(preload, "import { writeFileSync } from 'node:fs'; writeFileSync(" + JsonSerializer.Serialize(marker) + ", 'unexpected');").ConfigureAwait(true);
            first.StartEnvironment["NODE_OPTIONS"] = "--import=" + new Uri(preload).AbsoluteUri;
            first.StartEnvironment["npm_config_global"] = "true";
        }
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        string? nodePath = null;
        if (kind == EScriptPackageKind.Npm)
        {
            nodePath = NodeRuntime();
        }
        ScriptPackageChangeRequest request = new(Guid.NewGuid(), kind, new("contents.absent", string.Empty), Remove: true, NodePath: nodePath);
        ScriptPackageOperation accepted = await first.PostAsync<ScriptPackageOperation>("scripts.packages.change", request).ConfigureAwait(true);
        Assert.False(accepted.Completed);
        ScriptPackageOperation? finished = null;
        await CorePeer.WaitAsync(async token =>
        {
            finished = await first.GetAsync<ScriptPackageOperation>("scripts.packages.operation", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return finished.Completed;
        }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, finished!.Outcome);
        ScriptPackageCatalog remoteCatalog = await second.GetAsync<ScriptPackageCatalog>("scripts.packages.list").ConfigureAwait(true);
        Assert.Null(remoteCatalog.Nuget);
        Assert.Null(remoteCatalog.Npm);
        string code = "using System.Text;\npublic static string Run(string value) => new StringBuilder(value).ToString();";
        if (kind == EScriptPackageKind.Npm)
        {
            code = "export async function run(value = '') { await Promise.resolve(); return value; }";
        }
        ScriptAnalysis analysis = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest(language, code)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(finished.Revision, analysis.PackageEnvironmentId);
        System.Text.Json.Nodes.JsonObject settings = JsonSerializer.SerializeToNode(new { code, analysis }, ContractJson.Options)!.AsObject();
        string input = "arguments", text = "{\"value\":\"remote\"}";
        if (individual)
        {
            settings["inputMode"] = "individual";
            input = ScriptInputPorts.Name("value");
            text = "\"remote\"";
        }
        NodeDefinition node = new("script", "script." + language, JsonSerializer.SerializeToElement(settings), Target: new(EExecutionScope.One, ["a"]));
        NodeDefinition source = new("source", "value.constant", JsonSerializer.SerializeToElement(new { text }));
        WorkflowDefinition workflow = new("package-script", "Package script", [source, node], [new("source", "value", "script", input)]);
        WorkflowSaveResult saved = await second.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null, EActivationPolicy.Auto)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await RunAsync(second, workflow.Id).ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement current = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement[] ownPeers = current.GetProperty("peers").EnumerateArray().ToArray();
            Guid instance = current.GetProperty("instanceId").GetGuid();
            JsonElement remote = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return ownPeers.Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && remote.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("peerId").GetString() is "a"
                    && peer.GetProperty("instanceId").GetGuid() == instance && peer.GetProperty("status").GetString() is "ready");
        }).ConfigureAwait(true);
        ScriptPackageOperation duplicate = await first.PostAsync<ScriptPackageOperation>("scripts.packages.change", request).ConfigureAwait(true);
        Assert.True(duplicate.Completed);
        Assert.Equal(finished.Revision, duplicate.Revision);
        await RunAsync(second, workflow.Id).ConfigureAwait(true);
        Assert.False(File.Exists(marker));
    }

    /// <summary>別Peerへのノード配送と専用ワーカーの結果を照会します。</summary>
    /// <returns>完了した元実行の確認です。</returns>
    private static async Task RunAsync(CorePeer peer, string workflowId, CancellationToken cancellationToken = default)
    {
        WorkflowRunStatus run = await peer.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.NewGuid(), workflowId), cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token =>
        {
            run = await peer.GetAsync<WorkflowRunStatus>("runs.read", run.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !run.Running;
        }, cancellationToken).ConfigureAwait(false);
        Assert.True(run.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(run, ContractJson.Options));
        TargetNodeResult target = Assert.Single(run.Report!.Nodes.Single(node => node.NodeId is "script").Targets);
        Assert.Equal("a", target.PeerId);
        Assert.Equal("remote", target.Result.Outputs["value"].GetString());
    }

    /// <summary>明示した試験Node.jsを優先し、通常ビルドでも必要な開発用Node.jsを確認します。</summary>
    /// <returns>実Coreに指定する絶対パスです。</returns>
    private static string NodeRuntime()
    {
        string variable = "CONTENTS_TEST_NODE";
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured)
        {
            return configured;
        }
        string file = "node";
        if (OperatingSystem.IsWindows())
        {
            file += ".exe";
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("CONTENTS_TEST_NODEでNode.js 24以降を指定してください。");
    }
}
