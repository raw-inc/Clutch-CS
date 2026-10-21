using ReArtWord.Contents.BackgroundService.Client.Management;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>接続先で観測した状態だけを共有します。</summary>
internal sealed class ConnectionState(ManagementApi api)
{
    private int selectionVersion;

    /// <summary>到達したクラスタとPeerの組を明示して選択します。</summary>
    /// <returns>指定先への照会確認です。</returns>
    public Task SelectTargetAsync(string clusterId, string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        api.SelectedClusterId = clusterId;
        return SelectPeerAsync(peerId, cancellationToken);
    }

    /// <summary>切替を画面移動やCore再起動にせず接続先だけへ適用します。</summary>
    /// <returns>選択先の取得結果です。</returns>
    public async Task SelectPeerAsync(string? peerId, CancellationToken cancellationToken = default)
    {
        selectionVersion++;
        api.SelectedPeerId = peerId;
        if (string.IsNullOrWhiteSpace(peerId))
        {
            api.SelectedPeerId = null;
        }
        Current = null;
        Connected = false;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>最終取得状態です。切断後の過去値をReady証明に使いません。</summary>
    public ServiceStatus? Current
    {
        get; private set;
    }

    /// <summary>直近の取得ができたかです。</summary>
    public bool Connected
    {
        get; private set;
    }

    /// <summary>現物の状態を再取得します。</summary>
    /// <returns>取得確認の完了です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (api.SelectedClusterId is null || api.SelectedPeerId is null)
        {
            Current = null;
            Connected = false;
            return;
        }
        int generation = selectionVersion;
        try
        {
            ServiceStatus? result = await api.GetAsync<ServiceStatus>("/client/dashboard/status", cancellationToken).ConfigureAwait(false);
            if (generation != selectionVersion)
            {
                return;
            }
            if (result is not null && !string.Equals(result.PeerId, api.SelectedPeerId, StringComparison.Ordinal))
            {
                Current = null;
                Connected = false;
                return;
            }
            Current = result;
            Connected = Current is not null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (generation == selectionVersion)
            {
                Connected = false;
            }
        }
    }
}
