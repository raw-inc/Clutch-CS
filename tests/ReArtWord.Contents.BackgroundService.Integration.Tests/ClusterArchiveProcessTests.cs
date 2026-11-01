using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>CLI、ClientHost、2つのクラスターを起動し、全ピアへの復元を検証します。</summary>
public sealed class ClusterArchiveProcessTests
{
    private static readonly string[] expectedPeers = ["a", "b"];

    /// <summary>保存版・有効版・両Coreのログを別クラスターへ復元し、再起動後も有効版が変わらないことを確認します。</summary>
    /// <returns>書き出しから復元、再起動後の状態までの検証。</returns>
    [Fact]
    public async Task CliMovesSettingsAndLogsAcrossClustersAndRestoresEveryActivePeer()
    {
        string sourceId = Guid.NewGuid().ToString("N"), destinationId = Guid.NewGuid().ToString("N");
        int[] ports = CorePeer.AvailableUdpPorts(2);
        int sourcePort = ports[0], destinationPort = ports[1];
        CorePeer sourceA = new(sourceId, "a", sourcePort, true, hostId: "host-a"), sourceB = new(sourceId, "b", sourcePort, false, hostId: "host-b");
        CorePeer destinationA = new(destinationId, "a", destinationPort, true, hostId: "host-a"), destinationB = new(destinationId, "b", destinationPort, false, hostId: "host-b");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceALifetime = sourceA.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceBLifetime = sourceB.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationALifetime = destinationA.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationBLifetime = destinationB.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        await sourceA.StartAsync(timeout.Token).ConfigureAwait(true);
        await sourceB.StartAsync(timeout.Token).ConfigureAwait(true);
        await destinationA.StartAsync(timeout.Token).ConfigureAwait(true);
        await destinationB.StartAsync(timeout.Token).ConfigureAwait(true);
        await WaitReadyAsync(sourceA, timeout.Token).ConfigureAwait(true);
        await WaitReadyAsync(destinationA, timeout.Token).ConfigureAwait(true);
        ClientHostProcess client = new(Path.Combine(sourceA.DirectoryPath, "archive-client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        await client.StartAsync(timeout.Token).ConfigureAwait(true);
        ClientNetworkBinding[] bindings = [new(sourceA.InterfaceId, "127.0.0.1", "127.255.255.255", sourcePort), new(sourceA.InterfaceId, "127.0.0.1", "127.255.255.255", destinationPort)];
        using (HttpResponseMessage configured = await client.SendAsync(HttpMethod.Put, "client/networks", bindings, cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            configured.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(false);
            DiscoveredCluster[] clusters = (await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(false))!;
            return clusters.Sum(cluster => cluster.Peers.Count) == 4;
        }, timeout.Token).ConfigureAwait(true);
        WorkflowSaveResult active = await sourceA.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("screen", 1), null), timeout.Token).ConfigureAwait(true);
        WorkflowSaveResult draft = await sourceA.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("screen", 2) with { Enabled = false }, active.Revision, EActivationPolicy.Explicit), timeout.Token).ConfigureAwait(true);
        WorkflowDefinition logging = new("archive-log", "Archive log", [new("log", "control.log", JsonSerializer.SerializeToElement(new { level = "INFO", message = "archive source event" }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(1) }, new(EExecutionScope.All, ["a", "b"]))], [], Enabled: true);
        Assert.Equal(EOutcome.Succeeded, (await sourceA.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), logging, null), timeout.Token).ConfigureAwait(true)).Activation.Outcome);
        Guid run = Guid.NewGuid();
        await sourceA.PostAsync<JsonElement>("runs.start", new WorkflowRunRequest(run, logging.Id), timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => !(await sourceA.GetAsync<JsonElement>("runs.read", run, token).ConfigureAwait(false)).GetProperty("running").GetBoolean(), timeout.Token).ConfigureAwait(true);
        await destinationA.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), Definition("extra", 3), null), timeout.Token).ConfigureAwait(true);
        string path = Path.Combine(sourceA.DirectoryPath, "cluster.zip");
        (int exportCode, string exported) = await CliAsync(client, sourceId, ["export", "--file", path, "--logs", "true"], timeout.Token).ConfigureAwait(true);
        Assert.True(exportCode == 0, exported);
        using JsonDocument export = JsonDocument.Parse(exported);
        Guid exportId = export.RootElement.GetProperty("operationId").GetGuid();
        (ClusterArchiveConfiguration archive, _) = await ClusterArchiveFile.InspectAsync(path, timeout.Token).ConfigureAwait(true);
        Assert.Equal(expectedPeers, archive.LogPeers);
        List<WorkflowLogEntry> logs = [];
        await foreach (IReadOnlyList<WorkflowLogEntry> page in ClusterArchiveFile.ReadLogsAsync(path, timeout.Token).ConfigureAwait(true))
        {
            logs.AddRange(page.Where(entry => entry.Message is "archive source event"));
        }
        Assert.Equal(expectedPeers, logs.Select(entry => entry.PeerId).Order(StringComparer.Ordinal));
        JsonElement health = await sourceA.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage download = await client.SendAsync(HttpMethod.Get, "client/archives/" + exportId.ToString("D") + "/download?cluster=" + sourceId
            + "&peer=a&instance=" + health.GetProperty("instanceId").GetGuid().ToString("D"), cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            download.EnsureSuccessStatusCode();
            byte[] bytes = await download.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal(export.RootElement.GetProperty("response").GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        (int previewCode, string previewed) = await CliAsync(client, destinationId, ["preview", "--file", path, "--mode", "replace"], timeout.Token).ConfigureAwait(true);
        Assert.True(previewCode == 0, previewed);
        using JsonDocument preview = JsonDocument.Parse(previewed);
        ArchiveImportPreview plan = preview.RootElement.GetProperty("response").Deserialize<ArchiveImportPreview>(ContractJson.Options)!;
        Assert.Equal("extra", Assert.Single(plan.Deleted));
        (int importCode, string imported) = await CliAsync(client, destinationId, ["import", "--id", plan.OperationId.ToString("D"), "--confirmation", plan.Confirmation], timeout.Token).ConfigureAwait(true);
        Assert.True(importCode is 0 or 5, imported);
        ArchiveOperationResult? status = null;
        await CorePeer.WaitAsync(async token =>
        {
            status = await destinationA.GetAsync<ArchiveOperationResult>("archive.operation", plan.OperationId, token).ConfigureAwait(false);
            return status.Completed;
        }, timeout.Token).ConfigureAwait(true);
        Assert.True(status!.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(status, ContractJson.Options));
        foreach (CorePeer peer in new[] { destinationA, destinationB })
        {
            JsonElement restored = await peer.GetAsync<JsonElement>("workflows.read", "screen", timeout.Token).ConfigureAwait(true);
            Assert.Equal(draft.Revision, restored.GetProperty("saved").GetProperty("revision").GetString());
            Assert.Equal(active.Revision, restored.GetProperty("effectiveRevision").GetString());
            Assert.True(restored.GetProperty("effective").GetProperty("enabled").GetBoolean());
            Assert.DoesNotContain((await peer.GetAsync<WorkflowListPage>("workflows.list", timeout.Token).ConfigureAwait(true)).Entries, entry => entry.Id is "extra");
        }
        LogQueryResult restoredLogs = await destinationA.GetAsync<LogQueryResult>("logs.query", new LogQuery(Text: "archive source event"), timeout.Token).ConfigureAwait(true);
        Assert.Equal(logs.Select(entry => entry.Id).Order(), restoredLogs.Entries.Select(entry => entry.Id).Order());
        await destinationB.StopAsync(timeout.Token).ConfigureAwait(true);
        await destinationB.StartAsync(timeout.Token).ConfigureAwait(true);

        // 管理APIへの応答だけでは設定の復元完了を判断できないため、Readyになってから有効版を確認します。
        await WaitReadyAsync(destinationB, timeout.Token).ConfigureAwait(true);
        Assert.Equal(active.Revision, (await destinationB.GetAsync<JsonElement>("workflows.read", "screen", timeout.Token).ConfigureAwait(true)).GetProperty("effectiveRevision").GetString());
        (int duplicateCode, string duplicate) = await CliAsync(client, destinationId, ["import", "--id", plan.OperationId.ToString("D"), "--confirmation", plan.Confirmation], timeout.Token).ConfigureAwait(true);
        Assert.True(duplicateCode == 0, duplicate);
    }

    /// <summary>両ピアのクラスターへの参加が完了するまで待ちます。</summary>
    /// <returns>両ピアがReadyになるまでの待機。</returns>
    private static Task WaitReadyAsync(CorePeer peer, CancellationToken cancellationToken = default) => CorePeer.WaitAsync(async token =>
    {
        JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
        PeerInfo[] members = status.GetProperty("peers").Deserialize<PeerInfo[]>(ContractJson.Options)!;
        return members.Length == 2 && members.All(member => member.Status == EPeerStatus.Ready);
    }, cancellationToken);

    /// <summary>入力値で版を区別できる、試験用のWFを作成します。</summary>
    /// <returns>定数入力を持つWF。</returns>
    private static WorkflowDefinition Definition(string id, int value) => new(id, id, [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(value) })], [], Enabled: true);

    /// <summary>CLIを別プロセスで実行し、終了コードと応答を取得します。</summary>
    /// <returns>CLIの終了コードと応答。</returns>
    private static async Task<(int Code, string Output)> CliAsync(ClientHostProcess client, string cluster, string[] arguments, CancellationToken cancellationToken = default)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string name = "ReArtWord.Contents.BackgroundService.CommandLine";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        string executable = Path.GetFullPath("../../../../../src/ReArtWord.Contents.BackgroundService.CommandLine/bin/" + configuration + "/net10.0/" + name, AppContext.BaseDirectory);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--url", client.Address.AbsoluteUri, "--cluster", cluster, "--peer", "a", "archive" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("CLI did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await ProcessCompletion.WaitAsync(process, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = await error.ConfigureAwait(false);
            }
            return (process.ExitCode, text);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
