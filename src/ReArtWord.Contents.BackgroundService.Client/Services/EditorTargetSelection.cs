using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.Client.Services;

/// <summary>接続pickerとクラスタタブの草稿保全・明示選択を共通化します。</summary>
internal sealed class EditorTargetSelection(EditorDocument editor, BrowserStorage storage, ConnectionState connection, ManagementApi api) : IDisposable
{
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<string, string> previous = new(StringComparer.Ordinal);
    private readonly HashSet<string> automatic = new(StringComparer.Ordinal);

    /// <summary>この画面で最後に明示選択したPeerを取得します。</summary>
    /// <returns>前回のPeer、または未選択です。</returns>
    internal string? PreviousPeer(string cluster)
    {
        if (previous.TryGetValue(cluster, out string? peer))
        {
            return peer;
        }
        if (!automatic.Contains(cluster) && string.Equals(api.SelectedClusterId, cluster, StringComparison.Ordinal))
        {
            return api.SelectedPeerId;
        }
        return null;
    }

    /// <summary>クラスタタブの接続候補を、ピアの明示指定として記憶せず選択します。</summary>
    /// <returns>草稿を保全した接続先切替です。</returns>
    internal Task SelectClusterAsync(string cluster, string peer, Guid instance, CancellationToken cancellationToken = default)
        => SelectTargetAsync(cluster, peer, instance, false, cancellationToken);

    /// <summary>クリック時の実体を再確認してから元草稿を保持し切り替えます。</summary>
    /// <returns>指定した同じPeerへの到達確認です。</returns>
    internal Task SelectAsync(string cluster, string peer, Guid instance, CancellationToken cancellationToken = default)
        => SelectTargetAsync(cluster, peer, instance, true, cancellationToken);

    /// <summary>開始時の対象を固定し、明示したピアだけを次のクラスタ選択へ引き継ぎます。</summary>
    /// <returns>実体照合と草稿保存を終えた選択です。</returns>
    private async Task SelectTargetAsync(string cluster, string peer, Guid instance, bool explicitPeer, CancellationToken cancellationToken = default)
    {
        int generation = editor.Generation;
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != editor.Generation)
            {
                throw UiError.Attach(new InvalidOperationException("Another selection completed first. Review the candidates again."), "Another selection completed first. Review the candidates again.");
            }
            IReadOnlyList<DiscoveredCluster> observed = await api.GetAsync<IReadOnlyList<DiscoveredCluster>>("/client/clusters", cancellationToken).ConfigureAwait(false) ?? [];
            if (!EditorClusterChoice.IsSameInstance(observed, cluster, peer, instance) || generation != editor.Generation)
            {
                throw UiError.Attach(new InvalidOperationException("The observed instance of the selected peer has changed. Review the selection again."), "The observed instance of the selected peer has changed. Review the selection again.");
            }
            string? oldCluster = api.SelectedClusterId;
            string? oldPeer = api.SelectedPeerId;
            await editor.SwitchTargetAsync(cluster, peer, cancellationToken).ConfigureAwait(false);
            await storage.WriteAsync("contents.client.cluster", cluster, true, cancellationToken).ConfigureAwait(false);
            await storage.WriteAsync("contents.client.peer", peer, true, cancellationToken).ConfigureAwait(false);
            await connection.SelectTargetAsync(cluster, peer, cancellationToken).ConfigureAwait(false);
            if (oldCluster is not null && oldPeer is not null && !automatic.Contains(oldCluster))
            {
                previous[oldCluster] = oldPeer;
            }
            if (explicitPeer)
            {
                previous[cluster] = peer;
                automatic.Remove(cluster);
            }
            else
            {
                automatic.Add(cluster);
            }
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>画面スコープが終了した際に選択排他を解放します。</summary>
    public void Dispose() => changes.Dispose();
}
