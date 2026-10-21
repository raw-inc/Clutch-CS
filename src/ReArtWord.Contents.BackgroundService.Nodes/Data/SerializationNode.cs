using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>指定形式の構造化解析またはシリアライズを行います。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class SerializationNode : IRuntimeValueNode
{
    private const string FieldBinary = "binary";

    private readonly string format;
    private readonly bool serialize;
    private bool TypedParsing => !serialize && format is "json" or "yaml" or "toml";

    /// <summary>形式と処理方向を固定して登録します。</summary>
    public SerializationNode(string format, bool serialize)
    {
        this.format = format switch
        {
            "json" or "yaml" or "toml" or "csv" or "text" or "binary" => format,
            _ => throw new ArgumentOutOfRangeException(nameof(format), "未対応の登録種別です。"),
        };
        this.serialize = serialize;
        string operation = "parse";
        if (serialize)
        {
            operation = "serialize";
        }
        Descriptor = NodeSupport.Describe($"data.{format}.{operation}", $"{format} {operation}", "Data",
            """{"type":"object","properties":{}}""");
        EPortValueKind outputKind = EPortValueKind.Json;
        if (string.Equals(format, FieldBinary, StringComparison.Ordinal))
        {
            Descriptor = Descriptor with { SettingsSchema = BinaryLayoutCodec.AddFieldsSchema(Descriptor.SettingsSchema) };
            outputKind = EPortValueKind.Any;
            if (serialize)
            {
                outputKind = EPortValueKind.Binary;
            }
        }
        Descriptor = Descriptor with { IsValueOperation = true, Outputs = [new PortDescriptor("value", outputKind)] };
        if (TypedParsing)
        {
            Descriptor = Descriptor with { SettingsSchema = StructuredValueTypes.Schema(Descriptor.SettingsSchema, "value") };
        }
    }

    /// <summary>設定画面と配線検証用の情報です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>形式固有の設定を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (TypedParsing)
        {
            issues.AddRange(StructuredValueTypes.Validate(definition, "value"));
        }
        if (issues.Count == 0 && string.Equals(format, FieldBinary, StringComparison.Ordinal))
        {
            issues.AddRange(BinaryLayoutCodec.ValidateFields(definition));
        }
        return issues;
    }

    /// <summary>一件の入力を形式変換します。</summary>
    /// <returns>正常出力または解析・変換失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeValue? input = NodeSupport.Input(context);
        if (input is null)
        {
            return ValueTask.FromResult(NodeResult.Failure("data.input.missing", "入力がありません。", EOutcome.NotExecuted));
        }
        try
        {
            NodeValue output;
            if (string.Equals(format, FieldBinary, StringComparison.Ordinal))
            {
                JsonElement fields = new NodeSettings(context.Settings).Get("fields");
                if (serialize)
                {
                    output = BinaryLayoutCodec.Encode(input, fields);
                }
                else
                {
                    output = BinaryLayoutCodec.Decode(input, fields);
                }
            }
            else
            {
                if (serialize)
                {
                    output = StructuredDataCodec.Serialize(format, input);
                }
                else
                {
                    output = StructuredDataCodec.Parse(format, input);
                }
            }
            if (TypedParsing)
            {
                string prefix = "value";
                if (StructuredValueTypes.Read(context.Settings, context.Inputs, prefix) is { } type)
                {
                    ValueTypeOperations.ValidateValue(output, type);
                    output = PathTypeMetadata.Apply(output, type);
                }
            }
            return ValueTask.FromResult(NodeResult.Success(output));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException or OverflowException or YamlDotNet.Core.YamlException or Tomlyn.TomlException or CsvHelper.CsvHelperException)
        {
            // 解析器の例外本文には入力・秘密値が含まれるため公開しません。
            return ValueTask.FromResult(NodeResult.Failure("data.format.invalid", "入力またはフィールド定義を指定形式へ変換できません。"));
        }
    }
}
