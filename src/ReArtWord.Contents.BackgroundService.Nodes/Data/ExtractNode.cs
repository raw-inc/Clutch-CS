using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data;

/// <summary>構造化値、行、セル、文字列一致、バイト範囲を抽出します。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class ExtractNode : IRuntimeValueNode
{
    private const string FieldPointer = "pointer";
    private const string FieldPath = "path";
    private const string FieldRegex = "regex";
    private const string FieldPattern = "pattern";

    private const string FieldFields = "fields";

    /// <summary>抽出方法を設定画面へ公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.extract", "値の抽出", "Data",
        """{"type":"object","required":["mode"],"properties":{"mode":{"type":"string","enum":["pointer","row","cell","line","regex","binary"]},"path":{"type":"string","x-visibleWhen":{"property":"mode","values":["pointer"]},"x-requiredWhen":{"property":"mode","values":["pointer"]}},"row":{"type":"integer","minimum":0,"x-visibleWhen":{"property":"mode","values":["row","cell","line"]},"x-requiredWhen":{"property":"mode","values":["row","cell","line"]}},"column":{"type":"integer","minimum":0,"x-visibleWhen":{"property":"mode","values":["cell"]},"x-requiredWhen":{"property":"mode","values":["cell"]}},"pattern":{"type":"string","x-visibleWhen":{"property":"mode","values":["regex"]},"x-requiredWhen":{"property":"mode","values":["regex"]}},"group":{"type":"integer","minimum":0,"x-visibleWhen":{"property":"mode","values":["regex"]}},"offset":{"type":"integer","minimum":0,"x-visibleWhen":{"property":"mode","values":["binary"]}},"length":{"type":"integer","minimum":0,"x-visibleWhen":{"property":"mode","values":["binary"]}},"fields":{"type":"array","x-visibleWhen":{"property":"mode","values":["binary"]}}}}""",
        outputs: [new PortDescriptor("value"), new PortDescriptor("count", EPortValueKind.Json)]) with
    {
        IsValueOperation = true,
        Description = "JSON のパスや設定したレイアウトから値を取り出します。比較や送信に必要な項目を入力から選びます。",
    };

    /// <summary>共通のバイトレイアウト定義を公開します。</summary>
    public ExtractNode()
    {
        Descriptor = Descriptor with { SettingsSchema = BinaryLayoutCodec.AddFieldsSchema(Descriptor.SettingsSchema) };
    }

    /// <summary>指定方法と正規表現を検証します。</summary>
    /// <returns>不足・不正設定です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        NodeSettings settings = new(definition.Settings);
        string? mode = settings.ReadText("mode");
        string? required = mode switch
        {
            "pointer" => "path",
            "regex" => "pattern",
            "row" or "cell" or "line" => "row",
            _ => null,
        };
        if (required is not null && settings.Get(required).ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(new ValidationIssue("extract.setting.required", "抽出方法に必要な設定がありません。", definition.Id, required));
        }
        if (string.Equals(mode, FieldPointer, StringComparison.Ordinal) && settings.ReadText(FieldPath) is string path && !JsonPointer.IsValid(path))
        {
            issues.Add(new ValidationIssue("extract.pointer.invalid", "パスはJSON Pointerで指定してください。", definition.Id, "path"));
        }
        if (string.Equals(mode, FieldRegex, StringComparison.Ordinal) && settings.ReadText(FieldPattern) is string pattern)
        {
            try
            {
                Regex expression = new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                string groupSetting = "group";
                if (!expression.GetGroupNumbers().Contains(settings.ReadInt32(groupSetting)))
                {
                    issues.Add(new ValidationIssue("extract.group.invalid", "正規表現に指定グループがありません。", definition.Id, groupSetting));
                }
            }
            catch (ArgumentException)
            {
                issues.Add(new ValidationIssue("extract.regex.invalid", "正規表現が不正です。", definition.Id, "pattern"));
            }
        }
        string binaryMode = "binary";
        if (issues.Count == 0 && string.Equals(mode, binaryMode, StringComparison.Ordinal))
        {
            issues.AddRange(BinaryLayoutCodec.ValidateFields(definition));
        }
        string cellMode = "cell";
        string columnField = "column";
        if (string.Equals(mode, cellMode, StringComparison.Ordinal) && settings.Get(columnField).ValueKind == JsonValueKind.Undefined)
        {
            issues.Add(new ValidationIssue("extract.column.required", "セル抽出には列番号が必要です。", definition.Id, columnField));
        }
        return issues;
    }

    /// <summary>未存在を失敗にせず正常nullとして抽出します。</summary>
    /// <returns>値と抽出結果要素数です。</returns>
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
            return ValueTask.FromResult(NodeResult.Failure("extract.input.missing", "入力がありません。", EOutcome.NotExecuted));
        }
        try
        {
            NodeSettings settings = new(context.Settings);
            NodeValue value = settings.ReadText("mode") switch
            {
                "pointer" => Pointer(input, settings.ReadText("path")!),
                "row" => Element(input, settings.ReadInt32("row")),
                "cell" => Element(Element(input, settings.ReadInt32("row")), settings.ReadInt32("column")),
                "line" => Line(input, settings.ReadInt32("row")),
                "regex" => Match(input, settings.ReadText("pattern")!, settings.ReadInt32("group")),
                "binary" => Slice(input, settings),
                _ => throw new FormatException("抽出方法が不正です。"),
            };
            int count = 1;
            if (value.Kind == EValueKind.Json)
            {
                if (value.Json.ValueKind == JsonValueKind.Null)
                {
                    count = 0;
                }
                else
                {
                    if (value.Json.ValueKind == JsonValueKind.Array)
                    {
                        count = value.Json.GetArrayLength();
                    }
                }
            }
            return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["count"] = NodeValue.FromObject(count),
            }));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException or OverflowException or RegexMatchTimeoutException)
        {
            return ValueTask.FromResult(NodeResult.Failure("extract.invalid", "入力形式または抽出指定を処理できません。"));
        }
    }

    /// <summary>RFC6901形式の経路から値を取得します。</summary>
    /// <returns>対象値または正常nullです。</returns>
    internal static NodeValue Pointer(NodeValue input, string path)
    {
        return JsonPointer.Select(input, path);
    }

    /// <summary>指定した配列要素を取得します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Element(NodeValue value, int index)
    {
        if (value.Kind != EValueKind.Json)
        {
            throw new FormatException("配列が必要です。");
        }
        if (value.Json.ValueKind == JsonValueKind.Null)
        {
            return NodeValue.Null;
        }
        if (value.Json.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("配列が必要です。");
        }
        if (value.Json.GetArrayLength() <= index)
        {
            return NodeValue.Null;
        }
        return NodeValue.FromJson(value.Json[index]);
    }

    /// <summary>改行形式を統一して指定行を取得します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Line(NodeValue input, int row)
    {
        string[] lines = StructuredDataCodec.ReadText(input).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length <= row)
        {
            return NodeValue.Null;
        }
        return NodeValue.FromObject(lines[row]);
    }

    /// <summary>時間制限付き正規表現で一致を取得します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Match(NodeValue input, string pattern, int group)
    {
        Regex regex = new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!regex.GetGroupNumbers().Contains(group))
        {
            throw new FormatException("指定した正規表現グループがありません。");
        }
        string[] values = regex.Matches(StructuredDataCodec.ReadText(input)).Select(match => match.Groups[group].Value).ToArray();
        if (values.Length == 0)
        {
            return NodeValue.Null;
        }
        return NodeValue.FromObject(values);
    }

    /// <summary>指定バイト範囲またはフィールドを抽出します。</summary>
    /// <returns>検証または変換した結果です。</returns>
    private static NodeValue Slice(NodeValue input, NodeSettings settings)
    {
        if (settings.Get(FieldFields).ValueKind == JsonValueKind.Array)
        {
            return BinaryLayoutCodec.Decode(input, settings.Get("fields"));
        }
        int offset = settings.ReadInt32("offset");
        int length = settings.ReadInt32("length", input.Binary.Length - offset);
        if (input.Kind != EValueKind.Binary || input.Binary.Length < offset || length < 0 || input.Binary.Length - offset < length)
        {
            throw new FormatException("バイト範囲が不正です。");
        }
        return NodeValue.FromBytes(input.Binary.Slice(offset, length));
    }
}
