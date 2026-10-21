using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Comparison;
using ReArtWord.Contents.BackgroundService.Nodes.Data;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>取得不能を偽へ丸めず条件の三状態を分岐します。</summary>
// 抑制理由: 設定キーと操作名は既存ワークフローの互換性を維持する定数です。
[SuppressMessage("SourceQuality", "RAW410", Justification = "設定キーと登録済み操作名は互換性を維持するプロトコル定数です。")]
public sealed class ConditionNode : IActionNode
{
    private const string SettingStart = "start";
    private const string SettingEnd = "end";
    private const string SettingOperation = "operation";

    /// <summary>比較方法と三つの経路を宣言します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.condition", "条件分岐", "Control",
        """{"type":"object","required":["operation"],"properties":{"operation":{"type":"string","enum":["equal","notEqual","less","lessOrEqual","greater","greaterOrEqual","regex","boolean","timeWindow"]},"expected":{"x-visibleWhen":{"property":"operation","values":["equal","notEqual","less","lessOrEqual","greater","greaterOrEqual"]}},"pattern":{"type":"string","x-visibleWhen":{"property":"operation","values":["regex"]},"x-requiredWhen":{"property":"operation","values":["regex"]}},"start":{"type":"string","x-visibleWhen":{"property":"operation","values":["timeWindow"]},"x-requiredWhen":{"property":"operation","values":["timeWindow"]}},"end":{"type":"string","x-visibleWhen":{"property":"operation","values":["timeWindow"]},"x-requiredWhen":{"property":"operation","values":["timeWindow"]}}}}""",
        [new PortDescriptor("value", Required: true), new PortDescriptor("comparison"), new PortDescriptor("expected")],
        [new PortDescriptor("value"), new PortDescriptor("comparison"), new PortDescriptor("matched", EPortValueKind.Json), new PortDescriptor("result"), new PortDescriptor("reason")],
        ["true", "false", "indeterminate"]) with
    {
        Description = "comparison を expected と比較して true・false・indeterminate に分岐します。value は元の内容を保持して後続へ渡します。timeWindow は共通時刻で判定します。",
    };

    /// <summary>条件種別と設定を検証します。</summary>
    /// <returns>開始前の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        NodeSettings settings = new(definition.Settings);
        string? operation = settings.ReadText("operation");
        if (string.Equals(operation, RegexOperation, StringComparison.Ordinal))
        {
            try
            {
                _ = new Regex(settings.ReadText("pattern") ?? throw new ArgumentException("正規表現が必要です。", nameof(definition)), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                issues.Add(new ValidationIssue("condition.pattern.invalid", "有効な正規表現が必要です。", definition.Id));
            }
        }
        if (string.Equals(operation, TimeWindowOperation, StringComparison.Ordinal) && (!TryTime(settings.ReadText(SettingStart), out _) || !TryTime(settings.ReadText(SettingEnd), out _)))
        {
            issues.Add(new ValidationIssue("condition.window.invalid", "開始と終了をHH:mm:ssで指定してください。", definition.Id));
        }
        return issues;
    }

    private const string RegexOperation = "regex";
    private const string TimeWindowOperation = "timeWindow";

    /// <summary>元入力を選択された経路へだけ渡します。</summary>
    /// <returns>条件結果と比較に使用した実値です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeSettings settings = new(context.Settings);
        NodeValue? comparison = NodeSupport.Input(context, "comparison");
        NodeValue? original = NodeSupport.Input(context);
        bool? result = null;
        string reason = "condition.value.unavailable";
        try
        {
            if (string.Equals(settings.ReadText(SettingOperation), TimeWindowOperation, StringComparison.Ordinal))
            {
                DateTimeOffset now = TimeZoneInfo.ConvertTime(context.TimeProvider.GetUtcNow(), context.TimeZone ?? TimeZoneInfo.Utc);
                comparison = NodeValue.FromObject(now.ToString("O", CultureInfo.InvariantCulture));
                _ = TryTime(settings.ReadText("start"), out TimeOnly start);
                _ = TryTime(settings.ReadText("end"), out TimeOnly end);
                TimeOnly time = new TimeOnly(now.Hour, now.Minute, now.Second);
                result = start <= end && start <= time && time < end;
                if (end < start)
                {
                    result = start <= time || time < end;
                }
            }
            else
            {
                if (comparison is not null)
                {
                    JsonElement expectedSetting = settings.Get("expected");
                    NodeValue? expected = NodeSupport.Input(context, "expected");
                    if (expected is null && expectedSetting.ValueKind != JsonValueKind.Undefined)
                    {
                        expected = NodeValue.FromJson(expectedSetting);
                    }
                    result = Evaluate(comparison, expected, settings);
                    reason = "condition.value.incompatible";
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            reason = "condition.regex.timeout";
        }
        catch (FormatException)
        {
            reason = "condition.value.incompatible";
        }
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["result"] = NodeValue.FromObject(result),
            ["matched"] = NodeValue.FromObject(result),
        };
        if (original is not null)
        {
            outputs["value"] = original.Copy();
        }
        if (comparison is not null)
        {
            outputs["comparison"] = comparison.Copy();
        }
        string route = "indeterminate";
        if (result.HasValue)
        {
            route = "false";
            if (result.Value)
            {
                route = "true";
            }
        }
        else
        {
            outputs["reason"] = NodeValue.FromObject(new { kind = reason, message = "条件を判定できる値がありません。" });
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, route, outputs));
    }

    /// <summary>型を変換せず比較結果を求めます。</summary>
    /// <returns>比較可能なら真偽、不能ならnullです。</returns>
    private static bool? Evaluate(NodeValue value, NodeValue? expected, NodeSettings settings)
    {
        string operation = settings.ReadText("operation")!;
        if (string.Equals(operation, RegexOperation, StringComparison.Ordinal))
        {
            if (value.GetString() is not string text)
            {
                return null;
            }
            return Regex.IsMatch(text, settings.ReadText("pattern")!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        if (string.Equals(operation, BooleanOperation, StringComparison.Ordinal))
        {
            if (value.Kind == EValueKind.Json && value.Json.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.Json.GetBoolean();
            }
            return null;
        }
        if (expected is null)
        {
            return null;
        }
        return ValueComparison.Test(value, expected, operation);
    }

    private const string BooleanOperation = "boolean";

    /// <summary>文化依存なしで時刻設定を読み取ります。</summary>
    /// <returns>時刻が有効かを示します。</returns>
    private static bool TryTime(string? text, out TimeOnly time)
    {
        return TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}
