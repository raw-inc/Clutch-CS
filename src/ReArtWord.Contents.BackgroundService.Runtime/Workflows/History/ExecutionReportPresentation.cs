using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

/// <summary>実際の成否を維持し、巨大な値を実出力と別の要約へ置換します。</summary>
public static class ExecutionReportPresentation
{
    private static JsonSerializerOptions SerializationOptions
    {
        get;
    } = new(ContractJson.Options) { MaxDepth = 40 };

    /// <summary>保持済み要約も、小さいページの残容量へ合わせます。</summary>
    /// <returns>指定した上限の完全結果または要約です。</returns>
    public static ExecutionResultView Bound(ExecutionResultView view, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 8192);
        if (view.Report is not null)
        {
            return Bound(view.Report, maximumBytes);
        }
        ExecutionReportSummary summary = view.Summary ?? throw new ArgumentException("結果も要約もありません。", nameof(view));
        List<ExecutionTargetSummary> targets = summary.Targets.ToList();
        ExecutionResultView result = view with { Summary = summary with { Targets = targets } };
        while (maximumBytes < JsonSize.Measure(result, ContractJson.Options) && targets.Count != 0)
        {
            targets.RemoveAt(targets.Count - 1);
        }
        return result;
    }

    /// <summary>小さい完全結果、または変換不能・巨大な詳細を省いた要約を返します。</summary>
    /// <returns>通常管理応答と永続記録へ収まる結果です。</returns>
    public static ExecutionResultView Bound(ExecutionReport report, int maximumBytes = 131072)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 8192);
        string reason = "execution.details.size";
        try
        {
            ExecutionResultView complete = new(report);
            if (JsonSize.Measure(complete, SerializationOptions) <= maximumBytes)
            {
                return complete;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            reason = "execution.details.serialization";
        }
        List<ExecutionTargetSummary> targets = [];
        foreach (NodeExecutionReport node in report.Nodes)
        {
            foreach (TargetNodeResult target in node.Targets.Take(Math.Max(0, 100 - targets.Count)))
            {
                targets.Add(new(Short(node.NodeId), Short(target.PeerId), target.Result.Outcome,
                    Short(target.Result.ReasonCode), Short(target.Result.Message)));
            }
        }
        ExecutionReportSummary summary = new(report.Id, Short(report.WorkflowId), Short(report.Revision), report.Outcome,
            report.StartedAt, report.CompletedAt, targets, report.Nodes.Count, report.Nodes.Sum(static node => node.Targets.Count));
        ExecutionResultView view = new(null, summary, reason);
        while (maximumBytes < JsonSize.Measure(view, ContractJson.Options) && targets.Count != 0)
        {
            targets.RemoveAt(targets.Count - 1);
        }
        return view;
    }

    /// <summary>自由記述が要約全体を巨大化させないよう長さを制限します。</summary>
    /// <returns>省略記号を含む短い文字列です。</returns>
    private static string Short(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }
        if (256 < text.Length)
        {
            return text[..256] + "…";
        }
        return text;
    }
}
