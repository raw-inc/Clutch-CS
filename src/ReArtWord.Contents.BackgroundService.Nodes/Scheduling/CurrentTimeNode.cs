using System.Globalization;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scheduling;

/// <summary>実行時の現在時刻を一度取得し、明示したタイムゾーンと書式で文字列にします。</summary>
public sealed class CurrentTimeNode : IRuntimeValueNode
{
    /// <summary>UTCを既定とし、タイムゾーンと書式は設定口からも指定できます。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("scheduling.now", "Current date and time", "Scheduling", """
        {"type":"object","properties":{
          "timeZoneId":{"type":"string","minLength":1,"default":"UTC","x-runtime-input":true,"description":"Time zone for the result, for example UTC, Asia/Tokyo or Tokyo Standard Time. Defaults to UTC independently of the PC and cluster settings. Daylight saving rules use the execution PC's time zone data."},
          "format":{"type":"string","minLength":1,"maxLength":4096,"default":"yyyy-MM-dd HH:mm:ss.fff","x-runtime-input":true,"description":"Date/time format, for example HH:mm:ss.fff-YYYY/MM/DD. Uses .NET date/time formats with invariant culture; YYYY, YY and DD also mean yyyy, yy and dd outside quoted or escaped literals. MM is month, mm is minute. Use quotes for literal text."}}}
        """, inputs: [], outputs: [new("value", EPortValueKind.Json) { DataType = new("string") }]) with { IsValueOperation = true };

    /// <summary>PC上のタイムゾーン定義と書式を、時刻を取得せず検証します。</summary>
    /// <returns>保存または実行前に訂正できる設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (issues.Count != 0)
        {
            return issues;
        }
        NodeSettings settings = new(definition.Settings);
        try
        {
            _ = Zone(settings);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            issues.Add(new("time.zone.invalid", "実行PCで利用できるタイムゾーン識別子を指定してください。例: UTC、Asia/Tokyo。", definition.Id, "/timeZoneId"));
        }
        try
        {
            _ = DateTimeOffset.UnixEpoch.ToString(Format(settings), CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            issues.Add(new("time.format.invalid", "日時の書式が不正です。引用符・エスケープ・小数秒の桁数を確認してください。", definition.Id, "/format"));
        }
        return issues;
    }

    /// <summary>一件の結果を固定し、後続の参照ごとに時刻を取り直しません。</summary>
    /// <returns>指定地域の日時文字列です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        NodeSettings settings = new(context.Settings);
        DateTimeOffset instant = context.TimeProvider.GetUtcNow();
        DateTimeOffset local = TimeZoneInfo.ConvertTime(instant, Zone(settings));
        return ValueTask.FromResult(NodeResult.Success(NodeValue.FromObject(local.ToString(Format(settings), CultureInfo.InvariantCulture))));
    }

    /// <summary>暗黙にPCのローカル時刻へ置き換えず、設定のUTC既定を適用します。</summary>
    /// <returns>実行PCで解決したタイムゾーンです。</returns>
    private static TimeZoneInfo Zone(NodeSettings settings) => TimeZoneInfo.FindSystemTimeZoneById(settings.ReadText("timeZoneId", "UTC")!);

    /// <summary>引用・エスケープを保ち、利用者向けの年・日の別表記だけを正規化します。</summary>
    /// <returns>標準の日時書式へ渡す文字列です。</returns>
    private static string Format(NodeSettings settings)
    {
        string format = settings.ReadText("format", "yyyy-MM-dd HH:mm:ss.fff")!;
        StringBuilder result = new(format.Length);
        char quote = '\0';
        for (int index = 0; index < format.Length; index++)
        {
            char current = format[index];
            if (current == '\\')
            {
                result.Append(current);
                if (index + 1 < format.Length)
                {
                    result.Append(format[++index]);
                }
                continue;
            }
            if (current == quote)
            {
                quote = '\0';
                result.Append(current);
                continue;
            }
            if (quote == '\0' && current is '\'' or '"')
            {
                quote = current;
            }
            if (quote == '\0' && current is 'Y' or 'D')
            {
                int count = 1;
                while (index + count < format.Length && format[index + count] == current)
                {
                    count++;
                }
                if (current == 'Y' && count is 2 or 4 || current == 'D' && count == 2)
                {
                    current = char.ToLowerInvariant(current);
                }
                result.Append(current, count);
                index += count - 1;
                continue;
            }
            result.Append(current);
        }
        return result.ToString();
    }
}
