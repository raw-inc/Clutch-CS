using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>宣言と接続から成功出力の型だけを推測します。ノードや外部処理は実行しません。</summary>
public sealed class WorkflowTypeInference
{
    private readonly Dictionary<string, NodeDefinition> nodes;
    private readonly Dictionary<string, NodeDescriptor> catalog;
    private readonly ILookup<(string Node, string Port), WireDefinition> incoming;
    private readonly Dictionary<(string Node, string Port, bool Outbound), ValueTypeDefinition?> known = [];
    private readonly WorkflowPropertyTypes properties;
    private readonly HashSet<(string Node, string Port, bool Outbound)> visiting = [];
    private readonly WorkflowLiteralValues literals;
    private readonly ConfiguredOutputTypes configured;
    private readonly WorkflowSettingValues settings;
    private static string ValuePort => "value";
    private static string AnyType => "any";

    /// <summary>同じ設定版の接続と宣言を一度ずつ解決します。</summary>
    public WorkflowTypeInference(WorkflowDefinition document, IReadOnlyList<NodeDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(descriptors);
        nodes = document.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        catalog = descriptors.GroupBy(node => node.Type, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        incoming = document.Wires.ToLookup(wire => (wire.ToNode, wire.ToPort));
        literals = new(nodes, incoming);
        settings = new(document);
        properties = new(nodes, settings);
        configured = new(literals, PortType, settings);
    }

    /// <summary>同じグラフの索引を共有し、宣言と推論結果の範囲だけを元の単一種類へ限定します。</summary>
    private WorkflowTypeInference(WorkflowTypeInference shared, NodeDescriptor descriptor)
    {
        nodes = shared.nodes;
        incoming = shared.incoming;
        literals = shared.literals;
        settings = shared.settings;
        properties = shared.properties;
        catalog = new(StringComparer.Ordinal) { [descriptor.Type] = descriptor };
        configured = new(literals, PortType, settings);
    }

    /// <summary>設定反映後のポート宣言だけを使う既存の推論を、グラフ索引の複製なしで作ります。</summary>
    /// <returns>結果キャッシュと再帰検出を独立させた推論です。</returns>
    internal WorkflowTypeInference WithDescriptor(NodeDescriptor descriptor) => new(this, descriptor);

    /// <summary>接続元だけを調べ、接続先の旧配線を受入契約に使いません。</summary>
    /// <returns>安全に分かる出力型、または未確定です。</returns>
    public ValueTypeDefinition? OutputType(WireDefinition wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ValueTypeDefinition? selected = ValueTypeInference.Select(PortType(wire.FromNode, wire.FromPort, true), wire.Path);
        if (selected is null && !string.IsNullOrEmpty(wire.Path))
        {
            return new("any");
        }
        return selected;
    }

    /// <summary>配列の明示要素型を旧入力の実測型とは分けて取得します。</summary>
    /// <returns>確認できた要素の契約です。</returns>
    public ValueTypeDefinition? ArrayElementType(string nodeId)
    {
        if (nodes.TryGetValue(nodeId, out NodeDefinition? node))
        {
            return StructuredTypeSettings.Read(node, settings, "element");
        }
        return null;
    }

    /// <summary>JSON本文の候補を含む同じ親出力の構造を、個別ポートごとに解析し直しません。</summary>
    /// <returns>プロパティ口に利用できる構造です。本文全体の実行時型とは区別します。</returns>
    public ValueTypeDefinition? PropertyType(string nodeId, string port) => properties.HttpJson(nodeId, port) ?? PortType(nodeId, port, true);

    /// <summary>不正な編集中グラフの循環や深すぎる接続は未確定として扱います。</summary>
    /// <returns>宣言を保持した推測結果です。</returns>
    public ValueTypeDefinition? PortType(string nodeId, string port, bool outbound)
    {
        (string Node, string Port, bool Outbound) key = (nodeId, port, outbound);
        if (known.TryGetValue(key, out ValueTypeDefinition? cached))
        {
            return cached;
        }

        if (64 <= visiting.Count || !visiting.Add(key))
        {
            return null;
        }

        ValueTypeDefinition? result = null;
        if (nodes.TryGetValue(nodeId, out NodeDefinition? node))
        {
            node = settings.Resolve(node);
            if (outbound)
            {
                result = StartSignalPorts.IsOutput(catalog.GetValueOrDefault(node.Type), port) ? new("boolean")
                                                                                               : CompletionPorts.PortType(catalog.GetValueOrDefault(node.Type), port) ?? SubworkflowPorts.PortType(catalog.GetValueOrDefault(node.Type), node, port, true) ?? Scripting.ScriptSettings.PortType(node, port, true) ?? OscPorts.PortType(node, port) ?? HttpResponseTypes.Get(node, port, settings) ?? Output(node, port) ?? BuiltinOutputTypes.Get(node.Type, port) ?? ValueTypeInference.DeclaredOutput(catalog.GetValueOrDefault(node.Type), port);
            }
            else
            {
                result = Scripting.ScriptSettings.PortType(node, port, false) ?? OscPorts.PortType(node, port) ?? Input(node, port) ?? SubworkflowPorts.PortType(catalog.GetValueOrDefault(node.Type), node, port, false);
            }
        }
        visiting.Remove(key);
        known[key] = result;
        return result;
    }

    /// <summary>接続を優先し、定数がある場合は実JSON区分を使います。</summary>
    /// <returns>宣言を保持した推測結果です。</returns>
    private ValueTypeDefinition? Input(NodeDefinition node, string port)
    {
        WireDefinition[] wires = incoming[(node.Id, port)].Take(2).ToArray();
        if (wires.Length == 1)
        {
            return OutputType(wires[0]);
        }

        if (wires.Length != 0)
        {
            return null;
        }

        if (node.Inputs?.GetValueOrDefault(port) is { } value)
        {
            return FromValue(value);
        }
        return null;
    }

    /// <summary>既知ノードの成功時契約だけを細分化し、任意拡張は宣言へ戻します。</summary>
    /// <returns>宣言を保持した推測結果です。</returns>
    private ValueTypeDefinition? Output(NodeDefinition node, string port)
    {
        string resultPort = "result";
        string sourcePort = "source";
        if (PropertyPorts.Parse(port) is { } field)
        {
            if (properties.HttpJson(node.Id, field.Output) is { } json)
            {
                ValueTypeDefinition? property = ValueTypeInference.Select(json, PropertyPorts.Name(string.Empty, field.Property));
                return (property ?? new("any")) with { Optional = true };
            }
            return ValueTypeInference.Select(PortType(node.Id, field.Output, true), PropertyPorts.Name(string.Empty, field.Property));
        }
        NodeDescriptor? descriptor = catalog.GetValueOrDefault(node.Type);
        if (string.Equals(port, resultPort, StringComparison.Ordinal) || (descriptor?.Kind == ENodeKind.Source && string.Equals(port, sourcePort, StringComparison.Ordinal)))
        {
            return new("object");
        }
        if (IterationNodeTypes.IsStart(node.Type))
        {
            string itemPort = "item";
            if (string.Equals(port, itemPort, StringComparison.Ordinal))
            {
                string elementPrefix = "element";
                ValueTypeDefinition? declared = StructuredTypeSettings.Read(node, settings, elementPrefix);
                if (declared is not null || !StructuredTypeSettings.Automatic(node, settings, elementPrefix))
                {
                    return declared;
                }
                return DictionaryValueTypes.IterationItem(Input(node, ValuePort));
            }
            return port switch
            {
                "index" or "count" => new("integer"),
                _ => null,
            };
        }
        if (IterationNodeTypes.IsEnd(node.Type) && 0 < ValuePortPairs.Index(port))
        {
            ValueTypeDefinition? element = Input(node, port);
            return element?.KeyType is not null ? element with { Properties = null }
                                                : new("array", element);
        }
        string query = "query";
        string body = "body";
        string http = "http.listen";
        if (string.Equals(node.Type, http, StringComparison.Ordinal) && (string.Equals(port, query, StringComparison.Ordinal) || string.Equals(port, body, StringComparison.Ordinal)))
        {
            return configured.HttpParameter(node, port);
        }
        if (string.Equals(node.Type, ButtonValuePorts.NodeType, StringComparison.Ordinal) && 0 < ButtonValuePorts.Index(port))
        {
            return PortType(node.Id, port, false);
        }
        if (!string.Equals(port, ValuePort, StringComparison.Ordinal))
        {
            return null;
        }
        switch (node.Type)
        {
            case "value.comment" or "control.pass" or "control.log" or "control.delay" or "control.publish" or "control.condition" or "control.concurrencyLimit" or "control.concurrencyLimit.end" or "ai.content.route" or "ai.destination.route":
                return PortType(node.Id, ValuePort, false);
            case "value.code":
                return new("string");
            case "value.constant":
                string typePort = "type";
                if (HasInput(node, typePort))
                {
                    return settings.InputType(node, typePort);
                }
                string textSetting = "text";
                if (HasSetting(node, typePort) && Text(node, typePort) is null)
                {
                    return new("any");
                }
                string type = Text(node, typePort) ?? "auto";
                string automatic = "auto";
                if (string.Equals(type, automatic, StringComparison.Ordinal) || string.Equals(type, AnyType, StringComparison.Ordinal))
                {
                    if (HasSetting(node, textSetting) && Text(node, textSetting) is null)
                    {
                        return new("any");
                    }
                    try
                    {
                        using JsonDocument parsed = JsonDocument.Parse(Text(node, "text") ?? string.Empty);
                        return ValueTypeInference.FromJson(parsed.RootElement, 0);
                    }
                    catch (JsonException)
                    {
                        if (string.Equals(type, automatic, StringComparison.Ordinal))
                        {
                            return new("string");
                        }
                        return null;
                    }
                }
                if (ValueTypeInference.KnownName(type))
                {
                    string[] structured = ["array", "object"];
                    if (structured.Contains(type, StringComparer.Ordinal) && literals.Output(node.Id) is { } literal)
                    {
                        return FromValue(literal);
                    }
                    return new(type);
                }
                return null;
            case "value.array":
                return ArrayType(node);
            case "value.keyValue":
                return ObjectType(node);
            case "value.dictionary":
                return DictionaryValueTypes.Infer(node, literals, PortType);
            case "value.type":
                return new("type definition");
            case "data.convert":
                string? converted = Text(node, "targetType");
                if (converted is not null)
                {
                    return new(converted);
                }
                return null;
            case "math.not":
                return ValueTypeInference.LogicResult(PortType(node.Id, ValuePort, false));
            case "math.ands":
            case "math.ors":
                return ValueTypeInference.LogicResult(PortType(node.Id, ValuePort, false)?.ElementType);
            case "math.and":
            case "math.or":
            case "math.nand":
            case "math.nor":
            case "math.xor":
                return ValueTypeInference.LogicResult(PortType(node.Id, "left", false), PortType(node.Id, "right", false));
        }
        return configured.Get(node);
    }

    /// <summary>未接続と型不明の接続を区別します。</summary>
    /// <returns>入力指定の有無です。</returns>
    private bool HasInput(NodeDefinition node, string port) => incoming[(node.Id, port)].Any() || node.Inputs?.ContainsKey(port) == true;

    /// <summary>キーが分かる場合は、その項目の型を後続の抽出へ渡します。</summary>
    /// <returns>分かっている項目だけを含むオブジェクト型です。</returns>
    private ValueTypeDefinition ObjectType(NodeDefinition node)
    {
        string keyPort = "key";
        ValueTypeDefinition value = PortType(node.Id, ValuePort, false) ?? new("any");
        ValueTypeDefinition? basis = PortType(node.Id, "base", false);
        ValueTypeDefinition common = DictionaryValues.Common((basis?.Properties?.Values ?? []).Append(value));
        ValueTypeDefinition dictionary = new("object", KeyType: new("string"), ValueType: common);
        NodeValue? key = HasInput(node, keyPort) ? literals.Input(node, keyPort)
                                                 : literals.Setting(node, keyPort);
        if (key?.Json.ValueKind != JsonValueKind.String || key.Json.GetString() is not { } name)
        {
            return dictionary;
        }
        Dictionary<string, ValueTypeDefinition> properties = new(basis?.Properties ?? new Dictionary<string, ValueTypeDefinition>(), StringComparer.Ordinal);
        if (128 <= properties.Count)
        {
            return dictionary with { ValueType = new("any") };
        }
        properties[name] = value;
        return dictionary with { Properties = properties, ValueType = DictionaryValues.Common(properties.Values) };
    }

    /// <summary>既存配列と追加要素が一致する場合だけ要素型を補足します。</summary>
    /// <returns>保守的な配列型です。</returns>
    private ValueTypeDefinition ArrayType(NodeDefinition node)
    {
        string item = "item";
        string items = "items";
        string elementType = "elementType";
        if (HasInput(node, elementType))
        {
            return new("array", settings.InputType(node, elementType));
        }
        string prefix = "element";
        ValueTypeDefinition? element = StructuredTypeSettings.Read(node, settings, prefix);
        if (element is not null || !StructuredTypeSettings.Automatic(node, settings, prefix))
        {
            return new("array", element);
        }
        if (HasInput(node, items))
        {
            element = PortType(node.Id, items, false)?.ElementType;
            if (element is null)
            {
                return new("array");
            }
        }
        if (HasInput(node, item))
        {
            ValueTypeDefinition? added = PortType(node.Id, item, false);
            if (HasInput(node, items) && !ValueTypeInference.Same(element, added))
            {
                return new("array");
            }
            element = added;
        }
        return new("array", element);
    }

    /// <summary>JSONとバイト列を区別します。</summary>
    /// <returns>確認できた型です。</returns>
    private static ValueTypeDefinition FromValue(NodeValue value) => PathTypeMetadata.TypeOf(value) ?? new("any");

    /// <summary>設定を変更せず文字列の指定だけ参照します。</summary>
    /// <returns>文字列指定、またはnullです。</returns>
    private string? Text(NodeDefinition node, string name)
        => literals.Setting(node, name) is { Kind: EValueKind.Json, Json.ValueKind: JsonValueKind.String } value ? value.Json.GetString()
                                                                                                                 : null;

    /// <summary>接続された設定に保存済みの旧値を使いません。</summary>
    /// <returns>この設定が接続で供給されるかです。</returns>
    private bool HasSetting(NodeDefinition node, string name) => HasInput(node, SettingPorts.Name(name));

}
