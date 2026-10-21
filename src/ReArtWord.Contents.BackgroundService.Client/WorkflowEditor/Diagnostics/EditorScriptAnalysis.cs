using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>入力待機・blur時の解析応答を、そのコードと画面世代にだけ適用します。</summary>
internal sealed class EditorScriptAnalysis(EditorDocument model, ManagementApi api, SettingChoiceProvider choices)
{
    /// <summary>特定できる実行先を優先し、複数対象では選択中Peerを解析先として明示します。</summary>
    /// <returns>解析を依頼するPeerです。</returns>
    internal string? Peer(NodeDefinition node) => choices.ResolvePeer(node) ?? api.SelectedPeerId;

    /// <summary>解析のためにコードを実行せず、入力値と配線も変更しません。</summary>
    /// <returns>現在の草稿に採用できた解析か、遅延応答ならnullです。</returns>
    internal async Task<ScriptAnalysis?> AnalyzeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        NodeDefinition? node = model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || model.Locks.Node(model.Draft.Document.Id, nodeId))
        {
            return null;
        }
        long generation = model.Generation;
        ScriptAnalysisRequest request = Request(node);
        string fingerprint = ContractJson.Hash(request);
        string? cluster = api.SelectedClusterId, peer = Peer(node);
        ScriptAnalysis? report;
        if (peer is null)
        {
            report = new(fingerprint, null, ["Select a peer for script analysis."]);
        }
        else
        {
            try
            {
                report = await api.PostForPeerAsync<ScriptAnalysis>("/api/scripts/analyze", request, peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                report = new(fingerprint, null, ["Script analysis could not be confirmed. Analyze again before saving."]);
            }
        }
        NodeDefinition? current = model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (report is null || current is null || generation != model.Generation || model.Locks.Node(model.Draft.Document.Id, nodeId)
            || !string.Equals(cluster, api.SelectedClusterId, StringComparison.Ordinal) || !string.Equals(peer, Peer(current), StringComparison.Ordinal)
            || !string.Equals(fingerprint, report.Fingerprint, StringComparison.Ordinal) || !string.Equals(fingerprint, ContractJson.Hash(Request(current)), StringComparison.Ordinal))
        {
            return null;
        }
        JsonObject settings = JsonNode.Parse(current.Settings.GetRawText())!.AsObject();
        settings["analysis"] = JsonSerializer.SerializeToNode(report, ContractJson.Options);
        model.ChangeNode(nodeId, item => item with { Settings = JsonSerializer.SerializeToElement(settings) });
        if (request.SyntaxOnly && report.Diagnostics.Count == 0)
        {
            string codePort = "$settings.code";
            string[] consumers = model.Draft.Document.Nodes.Where(item => ScriptSettings.IsScript(item.Type))
                .Where(item => model.Draft.Document.Wires.Any(wire => string.Equals(wire.ToNode, item.Id, StringComparison.Ordinal) && string.Equals(wire.ToPort, codePort, StringComparison.Ordinal)))
                .Where(item => string.Equals(Request(item).Code, request.Code, StringComparison.Ordinal)).Select(item => item.Id).ToArray();
            foreach (string consumer in consumers)
            {
                if (generation != model.Generation)
                {
                    break;
                }
                await AnalyzeAsync(consumer, cancellationToken).ConfigureAwait(true);
            }
        }
        return report;
    }

    /// <summary>接続されたコードを解析し、入力欄に残る古い直接記入を採用しません。</summary>
    /// <returns>現在の配線から確認できる解析要求です。</returns>
    private ScriptAnalysisRequest Request(NodeDefinition node)
    {
        NodeDefinition effective = new WorkflowSettingValues(model.Draft.Document).Resolve(node);
        return ScriptSettings.Request(effective.Type, effective.Settings);
    }
}
