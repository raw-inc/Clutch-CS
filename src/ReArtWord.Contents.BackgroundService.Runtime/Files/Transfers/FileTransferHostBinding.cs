using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

/// <summary>ワークフロー所属クラスタ内で転送元Hostを一回の配送先へ固定します。</summary>
internal static class FileTransferHostBinding
{
    /// <summary>共通PC選択規則で転送元を選び、予約と転送へ同じPeerを渡します。</summary>
    /// <returns>配送用定義と開始不能の理由です。</returns>
    internal static (NodeDefinition Node, NodeResult? Error) Resolve(NodeDefinition node, IPeerDirectory directory, DateTimeOffset now)
    {
        string type = "file.transfer";
        string key = "sourceHost";
        if (!string.Equals(node.Type, type, StringComparison.Ordinal) || !node.Settings.TryGetProperty(key, out JsonElement host))
        {
            return (node, null);
        }
        IReadOnlyList<PeerInfo> peers = directory.GetPeers();
        PcSelectionResolver resolver = new(peers, new Dictionary<string, Contracts.Resources.ResourceSample>(StringComparer.Ordinal), now, directory.LocalInstanceId);
        TargetResolution selected = resolver.Resolve([new PcSelection(HostIds: [host.GetString()!])], EPcExecutionMode.One);
        PeerInfo? source = null;
        if (selected.Peers.Count != 0)
        {
            source = selected.Peers[0];
        }
        if (source is null || !(PeerStatusRules.CanParticipate(source.Status) || source.Status == EPeerStatus.Detached && source.InstanceId == directory.LocalInstanceId))
        {
            return (node, NodeResult.Failure("file.sourceHostUnavailable", "転送元Hostをワークフロー所属クラスタで確認できません。", EOutcome.NotExecuted));
        }
        JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
        settings.Remove(key);
        settings["sourcePeer"] = source.PeerId;
        return (node with { Settings = JsonSerializer.SerializeToElement(settings) }, null);
    }
}
