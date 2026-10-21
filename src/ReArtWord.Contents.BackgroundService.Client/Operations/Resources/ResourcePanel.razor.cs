using System.Globalization;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>実測リソースの最新値と有限範囲の履歴を取得します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class ResourcePanel
{
    private static string MatchBytes => "bytes";
    private static string MatchPercent => "percent";

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    private ResourceSample? sample;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<ResourceSample> history = [];
    private bool historyOpen;
    private Func<string>? problem;

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override Task OnInitializedAsync() => RefreshAsync();

    /// <summary>対象となる実測値を取得不能と区別して表示します。</summary>
    /// <returns>実測値と単位、または取得不能の理由です。</returns>
    private string Value(ResourceMetric? metric)
    {
        if (metric?.Value is not double number)
        {
            return Ui.Format("Unavailable ({0})", metric?.Reason ?? Ui["Not retrieved"]);
        }

        if (string.Equals(metric.Unit, MatchBytes, StringComparison.Ordinal))
        {
            return (number / 1073741824d).ToString("N2", CultureInfo.CurrentCulture) + " GiB";
        }

        return number.ToString("N2", CultureInfo.CurrentCulture) + (string.Equals(metric.Unit, MatchPercent, StringComparison.Ordinal) ? " %"
                                                                                                                                       : " " + metric.Unit);
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    public async Task RefreshAsync()
    {
        try
        {
            sample = await Api.GetAsync<ResourceSample>("/api/resources", cancellationToken: UiCancellation).ConfigureAwait(true);
            problem = null;
        }
        catch (HttpRequestException)
        {
            problem = Ui.Deferred("Live measurements could not be retrieved.");
        }
    }

    /// <summary>選択範囲の実測履歴を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task HistoryAsync()
    {
        try
        {
            history = await Api.GetAsync<List<ResourceSample>>("/api/resources/history", cancellationToken: UiCancellation).ConfigureAwait(true) ?? [];
            historyOpen = true;
        }
        catch (HttpRequestException)
        {
            problem = Ui.Deferred("History could not be retrieved.");
        }
    }
}
