using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>有限の集合操作を既存の型・検証・実行結果へ接続します。</summary>
public sealed class CollectionNode : IRuntimeValueNode
{
    private readonly string operation;
    private readonly bool isJoin, isSplit, isExpand;
    private static string JoinOperation => "join";
    private static string SplitOperation => "split";
    private static string ExpandOperation => "expand";
    private static string FieldKey => "field";

    /// <summary>一つの集合操作の設定とポートを登録します。</summary>
    public CollectionNode(string operation)
    {
        this.operation = operation;
        isJoin = string.Equals(operation, JoinOperation, StringComparison.Ordinal);
        isSplit = string.Equals(operation, SplitOperation, StringComparison.Ordinal);
        isExpand = string.Equals(operation, ExpandOperation, StringComparison.Ordinal);
        string fields = operation switch
        {
            "join" => "\"leftPath\":{\"type\":\"string\"},\"rightPath\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"inner\",\"left\",\"right\",\"full\"]},\"matchNull\":{\"type\":\"boolean\"},",
            "group" or "split" => "\"path\":{\"type\":\"string\"},",
            "flatten" => "\"depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":32,\"default\":1},",
            "expand" => "\"field\":{\"type\":\"string\",\"minLength\":1},\"keepEmpty\":{\"type\":\"boolean\"},",
            _ => throw new ArgumentException("未知の集合操作です。", nameof(operation)),
        };
        if (isJoin || isSplit)
        {
            fields += "\"comparison\":{\"type\":\"string\",\"enum\":[\"equal\",\"notEqual\",\"less\",\"lessOrEqual\",\"greater\",\"greaterOrEqual\"]},";
        }
        string schema = "{\"type\":\"object\",\"properties\":{" + fields
            + "\"maxItems\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000,\"default\":10000},\"maxBytes\":{\"type\":\"integer\",\"minimum\":2,\"maximum\":67108864,\"default\":16777216},\"maxWork\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000000,\"default\":1000000}}}";
        PortDescriptor[] inputs = [new("value", EPortValueKind.Json, true)];
        PortDescriptor[] outputs = [new("value", EPortValueKind.Json)];
        if (isJoin)
        {
            inputs = [new("left", EPortValueKind.Json, true), new("right", EPortValueKind.Json, true)];
        }
        if (isSplit)
        {
            inputs = [new("value", EPortValueKind.Json, true), new("expected", EPortValueKind.Json, true)];
            outputs = [new("matched", EPortValueKind.Json), new("rejected", EPortValueKind.Json), new("indeterminate", EPortValueKind.Json)];
        }
        Descriptor = NodeSupport.Describe("data." + operation, "Collection " + operation, "Data", schema, inputs, outputs) with { IsValueOperation = true };
    }

    /// <summary>入力・出力と有限の処理上限です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>パスと操作に必要な項目を開始前に確認します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        NodeSettings settings = new(definition.Settings);
        foreach (string path in new[] { "path", "leftPath", "rightPath" })
        {
            string value = settings.ReadText(path, string.Empty)!;
            if (!JsonPointer.IsValid(value))
            {
                issues.Add(new("collection.pointer", "JSON Pointerでパスを指定してください。", definition.Id, path));
            }
        }
        if (isExpand && string.IsNullOrEmpty(settings.ReadText(FieldKey)))
        {
            issues.Add(new("collection.field", "展開する配列項目が必要です。", definition.Id, "field"));
        }
        return issues;
    }

    /// <summary>部分結果を成功として返さず、すべての出力を上限内で構成します。</summary>
    /// <returns>結果集合または確認済み失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            NodeSettings settings = new(context.Settings);
            CollectionBudget budget = new(settings, cancellationToken);
            JsonArray result;
            if (isJoin)
            {
                result = CollectionOperations.Join(Array(context, "left", budget), Array(context, "right", budget), settings, budget);
            }
            else
            {
                JsonElement input = Array(context, "value", budget);
                if (isSplit)
                {
                    NodeValue expected = NodeSupport.Input(context, "expected") ?? throw new FormatException("collection.expectedRequired");
                    if (expected.Kind != EValueKind.Json)
                    {
                        throw new FormatException("collection.expectedJsonRequired");
                    }
                    return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", CollectionOperations.Split(input, expected, settings, budget)));
                }
                result = operation switch
                {
                    "group" => CollectionOperations.Group(input, settings.ReadText("path", "")!, budget),
                    "flatten" => CollectionOperations.Flatten(input, settings.ReadInt32("depth", 1), budget),
                    "expand" => CollectionOperations.Expand(input, settings.ReadText("field")!, settings.ReadBoolean("keepEmpty"), budget),
                    _ => throw new InvalidOperationException("集合操作がありません。"),
                };
            }
            return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(result)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or OverflowException)
        {
            return ValueTask.FromResult(NodeResult.Failure("collection.invalid", exception.Message));
        }
    }

    /// <summary>入力の配列型とサイズを計算開始前に確認します。</summary>
    /// <returns>この実行が所有する入力配列です。</returns>
    private static JsonElement Array(NodeExecutionContext context, string port, CollectionBudget budget)
    {
        NodeValue value = NodeSupport.Input(context, port) ?? throw new FormatException("collection.inputRequired");
        if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("collection.arrayRequired");
        }
        budget.CheckInput(value.Json.GetArrayLength(), value.PayloadSize());
        return value.Json;
    }
}
