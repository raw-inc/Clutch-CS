using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>ファイル設定の役割と、そのパスを所有するピアを共通に判定します。</summary>
internal static class ResourceSettingContext
{
    /// <summary>Host単位の転送元設定です。</summary>
    internal static string SourceHost => "sourceHost";

    /// <summary>互換性を維持する旧Peer単位の転送元設定です。</summary>
    internal static string SourcePeer => "sourcePeer";

    /// <summary>転送元をHost選択へ変更するときだけ旧Peer設定を除きます。</summary>
    /// <returns>元の保存値を変更しない編集用コピーです。</returns>
    internal static System.Text.Json.Nodes.JsonObject EditSettings(System.Text.Json.JsonElement settings, string key, string raw)
    {
        System.Text.Json.Nodes.JsonObject values = System.Text.Json.Nodes.JsonNode.Parse(settings.GetRawText())!.AsObject();
        if (string.Equals(key, SourceHost, StringComparison.Ordinal) && raw.Length != 0)
        {
            values.Remove(SourcePeer);
        }
        return values;
    }

    /// <summary>HTTPのURLパスをファイル選択欄と取り違えません。</summary>
    /// <returns>ファイル・ディレクトリ選択を提供する項目か。</returns>
    internal static bool IsPath(string? type, string key) => (type, key) is ("database.sqlite", "path") or ("script.python", "pythonPath") or ("script.javascript", "nodePath") || type is not null
        && (type.StartsWith("file.", StringComparison.Ordinal) || type.StartsWith("directory.", StringComparison.Ordinal)
            || type is "value.file" or "value.directory" || type.StartsWith("media.", StringComparison.Ordinal) || type.StartsWith("ai.", StringComparison.Ordinal))
        && key is "path" or "sourcePath" or "destination" or "outputPath";

    /// <summary>転送元と転送先を区別し、接続値を保存値より優先します。</summary>
    /// <returns>一覧を要求する一つのピアです。</returns>
    internal static string? Peer(NodeDefinition node, string key, WorkflowDefinition document, SettingChoiceProvider choices)
    {
        WorkflowSettingValues values = new(document);
        string source = "sourcePath";
        if (string.Equals(key, source, StringComparison.Ordinal))
        {
            string? host = values.Setting(node, "sourceHost")?.GetString();
            if (host is not null)
            {
                return choices.ResolveHost(host);
            }
            return values.Setting(node, "sourcePeer")?.GetString();
        }
        string[] paths = ["value.file", "value.directory"];
        if (paths.Contains(node.Type, StringComparer.Ordinal))
        {
            string? peer = values.Setting(node, "peerId")?.GetString();
            if (!string.IsNullOrWhiteSpace(peer))
            {
                return peer;
            }
            HashSet<string?> owners = new(StringComparer.Ordinal);
            foreach (WireDefinition wire in document.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)))
            {
                NodeDefinition? consumer = document.Nodes.FirstOrDefault(item => string.Equals(item.Id, wire.ToNode, StringComparison.Ordinal));
                if (consumer is null || paths.Contains(consumer.Type, StringComparer.Ordinal))
                {
                    continue;
                }
                string? setting = Contracts.Nodes.SettingPorts.Property(wire.ToPort);
                if (setting is not null && IsPath(consumer.Type, setting))
                {
                    owners.Add(Peer(consumer, setting, document, choices));
                }
                string valuePort = "value";
                string media = "media.";
                if (string.Equals(wire.ToPort, valuePort, StringComparison.Ordinal) && consumer.Type.StartsWith(media, StringComparison.Ordinal))
                {
                    owners.Add(choices.ResolvePeer(consumer));
                }
            }
            if (owners.Count != 0)
            {
                if (owners.Count == 1)
                {
                    return owners.Single();
                }
                return null;
            }
        }
        return choices.ResolvePeer(node);
    }
}
