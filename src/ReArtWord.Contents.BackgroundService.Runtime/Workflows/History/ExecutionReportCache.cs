using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;

/// <summary>大きな入力の履歴がメモリへ無期限に残らないよう、件数と転送サイズで保持します。</summary>
internal sealed class ExecutionReportCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly int maximumCount;
    private readonly long maximumBytes;
    private long bytes;

    /// <summary>最近の診断結果として保持する上限を指定します。</summary>
    internal ExecutionReportCache(int maximumCount = 500, long maximumBytes = 64L * 1024 * 1024)
    {
        this.maximumCount = maximumCount;
        this.maximumBytes = maximumBytes;
    }

    /// <summary>実行結果を返す処理自体には影響せず、保持範囲だけを制限します。</summary>
    internal void Remember(ExecutionReport report)
    {
        ExecutionResultView view = ExecutionReportPresentation.Bound(report);
        ExecutionReport? retained = report;
        long size;
        try
        {
            size = JsonSize.Measure(report, ContractJson.Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            retained = null;
            size = JsonSize.Measure(view, ContractJson.Options);
        }
        if (retained is not null && view.Report is null)
        {
            size += JsonSize.Measure(view, ContractJson.Options);
        }
        if (maximumBytes < size)
        {
            retained = null;
            size = JsonSize.Measure(view, ContractJson.Options);
        }
        if (maximumBytes < size)
        {
            return;
        }
        using (gate.EnterScope())
        {
            if (entries.Remove(report.Id, out Entry? previous))
            {
                bytes -= previous.Size;
            }
            entries.Add(report.Id, new Entry(report.Id, report.StartedAt, retained, view, size));
            bytes += size;
            while (maximumCount < entries.Count || maximumBytes < bytes)
            {
                Entry oldest = entries.Values.MinBy(static entry => entry.StartedAt)!;
                entries.Remove(oldest.Id);
                bytes -= oldest.Size;
            }
        }
    }

    /// <summary>保持範囲内の実行だけを取得します。</summary>
    /// <returns>確認済み結果、または範囲外のnullです。</returns>
    internal ExecutionReport? Find(Guid id)
    {
        using (gate.EnterScope())
        {
            return entries.GetValueOrDefault(id)?.Report;
        }
    }

    /// <summary>保持中の診断結果を新しい順に取得します。</summary>
    /// <returns>転送サイズと件数の上限内の結果です。</returns>
    internal IReadOnlyList<ExecutionReport> Recent()
    {
        using (gate.EnterScope())
        {
            return entries.Values.Where(static item => item.Report is not null).Select(static item => item.Report!).OrderByDescending(static item => item.StartedAt).ToArray();
        }
    }

    /// <summary>完全結果を保持できなかった一件も、確定した要約を取得します。</summary>
    /// <returns>診断の有限保持範囲内の結果です。</returns>
    internal IReadOnlyList<ExecutionResultView> RecentViews()
    {
        using (gate.EnterScope())
        {
            return entries.Values.OrderByDescending(static item => item.StartedAt).Select(static item => item.View).ToArray();
        }
    }

    /// <summary>再計算せず保持サイズを解放するための一項目です。</summary>
    private sealed record Entry(Guid Id, DateTimeOffset StartedAt, ExecutionReport? Report, ExecutionResultView View, long Size);
}
