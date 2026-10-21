using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>コード評価なしで項目構造と単純な値対応を組み立てます。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class TransformNode : IRuntimeValueNode
{
    private const string FieldRename = "rename";
    private const string FieldTo = "to";
    private const string FieldConcat = "concat";
    private const string FieldMap = "map";
    private const string FieldCreate = "create";
    private const string FieldSet = "set";
    private const string FieldSelect = "select";
    private const string FieldPath = "$path";
    private const string FieldFrom = "from";

    /// <summary>構造編集の入力と操作を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.transform", "構造の変換", "Data",
        """{"type":"object","required":["operation"],"properties":{"operation":{"type":"string","enum":["create","set","remove","rename","select","concat","map"]},"fields":{"type":"object","x-visibleWhen":{"property":"operation","values":["create","set"]}},"names":{"type":"array","items":{"type":"string"},"x-visibleWhen":{"property":"operation","values":["remove","select"]},"x-requiredWhen":{"property":"operation","values":["remove","select"]}},"name":{"type":"string","x-visibleWhen":{"property":"operation","values":["set","rename"]},"x-requiredWhen":{"property":"operation","values":["set","rename"]}},"to":{"type":"string","x-visibleWhen":{"property":"operation","values":["rename"]},"x-requiredWhen":{"property":"operation","values":["rename"]}},"separator":{"type":"string","x-visibleWhen":{"property":"operation","values":["concat"]}},"values":{"type":"array","x-visibleWhen":{"property":"operation","values":["concat"]}},"mapping":{"type":"array","items":{"type":"object","required":["from","to"],"properties":{"from":{},"to":{}}},"x-visibleWhen":{"property":"operation","values":["map"]},"x-requiredWhen":{"property":"operation","values":["map"]}}}}""",
        [new PortDescriptor("value"), new PortDescriptor("replacement")]) with
    {
        IsValueOperation = true,
        Description = "入力の項目を変換・配置して新しい構造を組み立てます。外部機器へ渡すデータ形式の調整に使います。",
    };

    /// <summary>操作に必要な項目を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        NodeSettings settings = new(definition.Settings);
        string? operation = settings.ReadText("operation");
        string? field = operation switch
        {
            "set" or "rename" => "name",
            "remove" or "select" => "names",
            "map" => "mapping",
            _ => null,
        };
        if (field is not null && settings.Get(field).ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(new ValidationIssue("transform.setting.required", "操作に必要な設定がありません。", definition.Id, field));
        }
        if (string.Equals(operation, FieldRename, StringComparison.Ordinal) && settings.Get(FieldTo).ValueKind != JsonValueKind.String)
        {
            issues.Add(new ValidationIssue("transform.setting.required", "変更先名が必要です。", definition.Id, "to"));
        }
        return issues;
    }

    /// <summary>入力を変更せず新しい構造を作成します。</summary>
    /// <returns>変換結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            NodeSettings settings = new(context.Settings);
            NodeValue input = NodeSupport.Input(context) ?? NodeValue.Null;
            string operation = settings.ReadText("operation")!;
            if (string.Equals(operation, FieldConcat, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Success(Concat(input, settings)));
            }
            if (string.Equals(operation, FieldMap, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Success(Map(input, settings.Get("mapping"))));
            }
            JsonObject output;
            if (string.Equals(operation, FieldCreate, StringComparison.Ordinal))
            {
                output = [];
            }
            else
            {
                output = JsonNode.Parse(input.Json.GetRawText()) as JsonObject ?? throw new FormatException("オブジェクト入力が必要です。");
            }
            if (operation is FieldCreate or FieldSet)
            {
                ApplyFields(output, input, settings.Get("fields"));
                if (string.Equals(operation, FieldSet, StringComparison.Ordinal))
                {
                    NodeValue replacement = NodeSupport.Input(context, "replacement") ?? throw new FormatException("replacement入力が必要です。");
                    if (replacement.Kind != EValueKind.Json)
                    {
                        throw new FormatException("Binary値はJSON項目へ暗黙変換できません。");
                    }
                    output[settings.ReadText("name")!] = JsonNode.Parse(replacement.Json.GetRawText());
                }
            }
            else
            {
                if (string.Equals(operation, FieldRename, StringComparison.Ordinal))
                {
                    string from = settings.ReadText("name")!;
                    string to = settings.ReadText("to")!;
                    if (!string.Equals(from, to, StringComparison.Ordinal) && output.TryGetPropertyValue(from, out JsonNode? value))
                    {
                        if (output.ContainsKey(to))
                        {
                            throw new FormatException("変更先項目が既に存在します。");
                        }
                        output.Remove(from);
                        output.Add(to, value);
                    }
                }
                else
                {
                    HashSet<string> names = settings.Get("names").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
                    foreach (string name in output.Select(pair => pair.Key).ToArray())
                    {
                        bool remove = names.Contains(name);
                        if (string.Equals(operation, FieldSelect, StringComparison.Ordinal))
                        {
                            remove = !remove;
                        }
                        if (remove)
                        {
                            output.Remove(name);
                        }
                    }
                }
            }
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(output)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return ValueTask.FromResult(NodeResult.Failure("transform.invalid", "指定した構造変換を入力へ適用できません。"));
        }
    }

    /// <summary>固定値とパス参照を出力項目へ配置します。</summary>
    private static void ApplyFields(JsonObject output, NodeValue input, JsonElement fields)
    {
        if (fields.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }
        foreach (JsonProperty field in fields.EnumerateObject())
        {
            output[field.Name] = ResolveField(field.Value, input, 0);
        }
    }

    /// <summary>配列や入れ子の固定構造にも専用一項目のJSON Pointer参照を適用します。</summary>
    /// <returns>元の入力・設定と親を共有しないJSON値です。</returns>
    private static JsonNode? ResolveField(JsonElement field, NodeValue input, int depth)
    {
        int maximumDepth = 64;
        if (maximumDepth < depth)
        {
            throw new FormatException("構造の入れ子が深すぎます。");
        }
        if (field.ValueKind == JsonValueKind.Object)
        {
            if (field.EnumerateObject().Count() == 1 && field.TryGetProperty(FieldPath, out JsonElement path))
            {
                NodeValue value = ExtractNode.Pointer(input, path.GetString()!);
                if (value.Kind != EValueKind.Json)
                {
                    throw new FormatException("Binary値は構造項目へ暗黙変換できません。");
                }
                return JsonNode.Parse(value.Json.GetRawText());
            }
            JsonObject result = [];
            foreach (JsonProperty property in field.EnumerateObject())
            {
                result.Add(property.Name, ResolveField(property.Value, input, depth + 1));
            }
            return result;
        }
        if (field.ValueKind == JsonValueKind.Array)
        {
            JsonArray result = [];
            foreach (JsonElement value in field.EnumerateArray())
            {
                result.Add(ResolveField(value, input, depth + 1));
            }
            return result;
        }
        return JsonNode.Parse(field.GetRawText());
    }

    /// <summary>スカラー値を区切り文字で連結します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Concat(NodeValue input, NodeSettings settings)
    {
        JsonElement values = settings.Get("values");
        if (values.ValueKind == JsonValueKind.Undefined)
        {
            values = input.Json;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("連結には値配列が必要です。");
        }
        List<string> texts = [];
        foreach (JsonElement item in values.EnumerateArray())
        {
            NodeValue value = NodeValue.FromJson(item);
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(FieldPath, out JsonElement path))
            {
                value = ExtractNode.Pointer(input, path.GetString()!);
            }
            if (value.Kind != EValueKind.Json || value.Json.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                throw new FormatException("連結要素はスカラーで指定してください。");
            }
            texts.Add(value.Json.ToString());
        }
        return NodeValue.FromObject(string.Join(settings.ReadText("separator", ""), texts));
    }

    /// <summary>明示対応の出力値を取得します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Map(NodeValue input, JsonElement mapping)
    {
        if (input.Kind != EValueKind.Json)
        {
            throw new FormatException("値対応にはJSON入力が必要です。");
        }
        foreach (JsonElement entry in mapping.EnumerateArray())
        {
            if (JsonElement.DeepEquals(input.Json, entry.GetProperty(FieldFrom)))
            {
                return NodeValue.FromJson(entry.GetProperty("to"));
            }
        }
        return NodeValue.Null;
    }
}
