using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>保存媒体ごとの利用可否と未保存状態を表示します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class AvailabilityPanel
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    private AvailabilityListPage? page;
    private string? cluster, peer;
    private Func<string>? message;
    private DateTimeOffset? observed;
    private bool busy;

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    protected override Task OnInitializedAsync() => LoadAsync(false);

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadAsync(bool next)
    {
        busy = true;
        message = null;
        (string? SelectedClusterId, string? SelectedPeerId) owner = (Api.SelectedClusterId, Api.SelectedPeerId);
        try
        {
            if (next && owner != (cluster, peer))
            {
                throw new InvalidOperationException("The connection changed. Refresh from the first page.");
            }

            AvailabilityListPage? result = await Api.PostAsync<AvailabilityListPage>("/api/availability/query", new AvailabilityListQuery(Cursor: next ? page?.Cursor
                                                                                                                                                       : null), cancellationToken: UiCancellation).ConfigureAwait(true);
            if (owner != (Api.SelectedClusterId, Api.SelectedPeerId))
            {
                return;
            }

            page = result ?? throw new InvalidOperationException("Current storage status is unconfirmed.");
            (cluster, peer) = owner;
            observed = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            message = error is InvalidOperationException ? Ui.Deferred("{0}", error.Message)
                                                         : Ui.Deferred("Storage status could not be retrieved. Any remaining display is the last observation.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>未確認の版を区別して短い識別表示を作ります。</summary>
    /// <returns>版の先頭文字列、未確認ならダッシュです。</returns>
    private static string Revision(string? revision) => revision is null ? "—"
                                                                         : revision[..Math.Min(10, revision.Length)];

    /// <summary>オフライン時の利用方針に対応する表示を返します。</summary>
    /// <returns>設定された利用方針の表示キーです。</returns>
    private static string Offline(EAvailabilityOffline policy) => policy switch
    {
        EAvailabilityOffline.Unavailable => "Unavailable",
        EAvailabilityOffline.LocalUnconfirmed => "LocalUnconfirmed",
        _ => "View the last confirmed revision only"
    };

    /// <summary>未保存・ローカル未統合・過去値などの状態を表示文にまとめます。</summary>
    /// <returns>該当する状態の翻訳を区切り文字で結合した表示です。</returns>
    private string State(AvailabilityState state) => string.Join(" / ", new[] { state.IsUnsaved ? "Active but unsaved"
                                                                                                : null, state.CandidateIsUnsaved ? "Unsaved candidate"
                                                                                                                                 : null, state.IsLocalUnconfirmed ? "Local, not integrated"
                                                                                                                                                                  : null, state.IsStale ? "Last observed"
                                                                                                                                                                                        : null, state.FallbackBackend is not null ? "Fallback storage in use"
                                                                                                                                                                                                                                  : null }.Where(item => item is not null).Select(item => Ui[item!]));
}
