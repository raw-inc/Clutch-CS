using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreプロセスとUDPでHostを指定したファイル転送を確認します。</summary>
public sealed class HostFileTransferTests
{
    /// <summary>Host指定を解決して実ファイルを転送し、旧形式でも同じ通信を使用します。</summary>
    /// <returns>実転送と互換性の確認です。</returns>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, true)]
    public async Task TransferAcrossHostProcessesAsync(bool legacy, bool outsideRoots, bool dynamicPaths, bool typedPaths)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer source = new(cluster, "source-peer", port, true, "source-host");
        CorePeer destination = new(cluster, "destination-peer", port, false, "destination-host");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = ((IAsyncDisposable)destination).ConfigureAwait(true);
        await source.StartAsync().ConfigureAwait(true);
        await destination.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement first = await source.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement second = await destination.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return Ready(first) && Ready(second);
        }).ConfigureAwait(true);
        string area = "content";
        if (outsideRoots)
        {
            area = "outside-roots";
        }
        string sourcePath = Path.Combine(source.DirectoryPath, area, "original.bin");
        string destinationPath = Path.Combine(destination.DirectoryPath, area, "received.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        byte[] bytes = Enumerable.Range(0, 80000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, bytes).ConfigureAwait(true);
        Dictionary<string, object> settings = new(StringComparer.Ordinal)
        {
            ["sourceHost"] = "source-host", ["sourcePath"] = sourcePath, ["path"] = destinationPath, ["requestId"] = "host-transfer", ["failurePolicy"] = "rollbackAll",
        };
        TargetDefinition? target = null;
        if (legacy)
        {
            settings.Remove("sourceHost");
            settings["sourcePeer"] = "source-peer";
            target = new(EExecutionScope.One, Peers: ["destination-peer"]);
        }
        WorkflowDefinition workflow = new("host-transfer", "host transfer", [new("transfer", "file.transfer", JsonSerializer.SerializeToElement(settings), Target: target)], []);
        if (!legacy)
        {
            workflow = workflow with { Nodes = [new("pc", PcSelectionPorts.SelectNode, JsonSerializer.Deserialize<JsonElement>("""{"mode":"selected","hostIds":["destination-host"]}""")), .. workflow.Nodes], Wires = [new("pc", "value", "transfer", PcSelectionPorts.Execution)] };
        }
        if (dynamicPaths)
        {
            settings.Remove("sourcePath");
            settings.Remove("path");
            string transferId = "transfer";
            workflow = workflow with
            {
                Nodes = [RuntimePath("from", sourcePath, typedPaths), RuntimePath("to", destinationPath, typedPaths), .. workflow.Nodes.Select(node => string.Equals(node.Id, transferId, StringComparison.Ordinal) ? node with { Settings = JsonSerializer.SerializeToElement(settings) }
                                                                                                                                                                                                                    : node)],
                Wires = [.. workflow.Wires, new("from", "value", "transfer", SettingPorts.Name("sourcePath")), new("to", "value", "transfer", SettingPorts.Name("path"))],
            };
        }
        if (typedPaths)
        {
            string toId = "to";
            workflow = workflow with
            {
                Nodes = [.. workflow.Nodes, new("remote-path", "control.pass", JsonSerializer.SerializeToElement(new { }), Target: new(EExecutionScope.One, ["destination-peer"]))],
                Wires = [.. workflow.Wires.Where(wire => !string.Equals(wire.FromNode, toId, StringComparison.Ordinal)),
                    new("to", "value", "remote-path", "value"), new("remote-path", "value", "transfer", SettingPorts.Name("path"))],
            };
        }
        WorkflowSaveResult saved = await source.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        WorkflowRunRequest request = new(Guid.NewGuid(), workflow.Id, saved.Revision);
        WorkflowRunStatus result = await source.PostAsync<WorkflowRunStatus>("runs.start", request).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            result = await source.GetAsync<WorkflowRunStatus>("runs.read", request.OperationId.ToString("D"), token).ConfigureAwait(true);
            return !result.Running;
        }).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal("destination-peer", Assert.Single(result.Report!.Nodes.Single(node => string.Equals(node.NodeId, "transfer", StringComparison.Ordinal)).Targets).PeerId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destinationPath).ConfigureAwait(true));
        if (typedPaths)
        {
            NodeValue remote = Assert.Single(result.Report!.Nodes.Single(node => node.NodeId is "remote-path").Targets).Result.Outputs["value"];
            Assert.Equal("path", remote.DataType!.Type);
            Assert.Equal(PathValues.Normalize(destinationPath), remote.GetString());
        }
    }

    /// <summary>静的値の折畳みを通らず、一回の実行で文字列パスを供給します。</summary>
    /// <returns>通常Actionの変換出力です。</returns>
    private static NodeDefinition RuntimePath(string id, string path, bool typed)
    {
        string type = typed ? "path"
                            : "string";
        return new(id, "data.convert", JsonSerializer.SerializeToElement(new { targetType = type }),
            Inputs: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject(path) });
    }

    /// <summary>両実体が通常参加したことを確認します。</summary>
    /// <returns>転送可能な参加状態です。</returns>
    private static bool Ready(JsonElement status) => status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
}
