using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>編集とコンパイルで、接続済みの固定値からポート形状を確認します。</summary>
public sealed class WorkflowSettingValues
{
    private readonly WorkflowLiteralValues literals;

    /// <summary>外部処理を呼ばず、サイズ制限付きの値構築だけを共有します。</summary>
    public WorkflowSettingValues(WorkflowDefinition document)
    {
        ArgumentNullException.ThrowIfNull(document);
        literals = new(document.Nodes.Where(node => node is not null && !string.IsNullOrEmpty(node.Id)).GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
            document.Wires.Where(wire => wire is not null).ToLookup(wire => (wire.ToNode, wire.ToPort)));
    }

    /// <summary>設定の接続を優先して静的な値を取得します。</summary>
    /// <returns>確認できた値、または未確定です。</returns>
    public NodeValue? Setting(NodeDefinition node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        return literals.Setting(node, name);
    }

    /// <summary>型定義入力に通常データをつなぐ前に、既知の値を確認します。</summary>
    /// <returns>確認できた入力、または未確定です。</returns>
    public NodeValue? Input(NodeDefinition node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        return literals.Input(node, name);
    }

    /// <summary>固定された型定義入力を副作用なしで解釈します。</summary>
    /// <returns>確定できない場合はnullです。</returns>
    internal ValueTypeDefinition? InputType(NodeDefinition node, string port)
    {
        NodeValue? value = literals.Input(node, port);
        return value is null ? null
                             : ValueTypeInference.Descriptor(value);
    }

    /// <summary>未接続と、まだ値の分からない入力接続を区別します。</summary>
    /// <returns>固定入力または配線が存在するかです。</returns>
    public bool HasInput(NodeDefinition node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        return literals.HasInput(node, name);
    }

    /// <summary>純粋な値・型構築の確認済み結果を説明用に取得します。</summary>
    /// <returns>未確定や上限超過ではnullです。</returns>
    public NodeValue? Output(string nodeId) => literals.Output(nodeId);

    /// <summary>OSCのポート構成へ接続された設定を反映し、保存用定義は変更しません。</summary>
    /// <returns>描画・接続検証に使う一時的な定義です。</returns>
    public NodeDefinition Resolve(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string[] keys = node.Type switch
        {
            "script.csharp" or "script.csharp-source" or "script.python" or "script.javascript" => ["code", "pythonPath", "nodePath", "manualContract"],
            "value.type" => ["type"],
            "data.object.expand" => ["objectMode", "objectSchema", "objectType"],
            "control.forEach" or "control.parallelForEach" or "value.array" => ["elementMode", "elementSchema", "elementType"],
            "http.listen" => ["queryMode", "querySchema", "queryType", "bodyMode", "bodySchema", "bodyType", "bodyFormat"],
            "data.json.parse" or "data.yaml.parse" or "data.toml.parse" => ["valueMode", "valueSchema", "valueType"],
            _ => OscPorts.Supports(node.Type) ? ["argumentTypes"]
                                              : [],
        };
        if (node.Settings.ValueKind != JsonValueKind.Object || !keys.Any(key => literals.HasInput(node, SettingPorts.Name(key))))
        {
            return node;
        }
        JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
        foreach (string key in keys.Where(key => literals.HasInput(node, SettingPorts.Name(key))))
        {
            settings.Remove(key);
            if (literals.Setting(node, key) is { Kind: EValueKind.Json } value)
            {
                settings[key] = JsonNode.Parse(value.Json.GetRawText());
            }
        }
        return node with { Settings = JsonSerializer.SerializeToElement(settings) };
    }
}
