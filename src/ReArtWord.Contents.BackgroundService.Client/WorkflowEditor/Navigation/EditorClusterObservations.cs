using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>重複取得を抑え、失敗中だけ前回のクラスタ観測を警告付きで保持します。</summary>
internal sealed class EditorClusterObservations(ManagementApi api)
{
    private int refreshing;

    /// <summary>最後に正常取得できた集合です。</summary>
    // 抑制理由: 前回の描画が保持する集合を変更せず、正常取得した不変の集合参照へ交換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "前回観測を保持したまま、正常取得時だけ集合参照を交換します。")]
    internal IReadOnlyList<DiscoveredCluster> Clusters
    {
        get; private set;
    } = [];

    /// <summary>最新取得が失敗したかです。成功復帰時には解除します。</summary>
    internal bool Unavailable
    {
        get; private set;
    }

    /// <summary>手動操作と定期取得で共有する一件の取得状態です。</summary>
    internal bool Refreshing => Volatile.Read(ref refreshing) != 0;

    /// <summary>前回情報を破壊せず、終了済み画面の取消しを障害として表示しません。</summary>
    /// <returns>一件の観測更新です。取得中の再要求は次の定期確認へ委ねます。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
        {
            return;
        }
        try
        {
            IReadOnlyList<DiscoveredCluster> current = await api.GetAsync<List<DiscoveredCluster>>("/client/clusters", cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The cluster observation response is empty.");
            cancellationToken.ThrowIfCancellationRequested();
            Clusters = current;
            Unavailable = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面終了は取得先の障害ではなく、最後の観測を更新しません。
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            Unavailable = true;
        }
        finally
        {
            Volatile.Write(ref refreshing, 0);
        }
    }
}
