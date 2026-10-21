using System.Net;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>オンライン実取得と、明示したオフライン前回版の編集を分離します。</summary>
internal static class EditorWorkflowLoader
{
    /// <summary>新しい保存版か、取得不能時だけ手元の完全版を開きます。</summary>
    /// <returns>どの版を表示したかの説明です。</returns>
    internal static async Task<string> OpenAsync(EditorDocument model, ManagementApi api, BrowserStorage storage, string id, CancellationToken cancellationToken = default)
    {
        string? cluster = api.SelectedClusterId;
        string? peer = api.SelectedPeerId;
        int generation = model.Generation;
        try
        {
            WorkflowView view = await WorkflowReader.ReadAsync(api, id, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(model, api, cluster, peer, generation);
            StoredWorkflow saved = view.RequireSaved();
            bool retained = await EditorSavedCache.WriteAsync(storage, cluster, peer, saved, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(model, api, cluster, peer, generation);
            await model.LoadAsync(saved, cancellationToken).ConfigureAwait(false);
            if (!retained)
            {
                return "保存版を実取得しましたが、offline用の完全版を端末に保存できませんでした。";
            }
            if(saved.LocalOnly)
            {
                return "このPeerの局所保存版を取得しました。Clusterへ未共有です。再参加後に統合結果を確認してください。";
            }
            return string.Empty;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null or HttpStatusCode.RequestTimeout || 500 <= (int?)exception.StatusCode)
        {
            EnsureCurrent(model, api, cluster, peer, generation);
            return await OpenPreviousAsync(model, storage, cluster, peer, id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EnsureCurrent(model, api, cluster, peer, generation);
            return await OpenPreviousAsync(model, storage, cluster, peer, id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>遅延した読取りを別Peer・別ワークフローの草稿へ適用しません。</summary>
    private static void EnsureCurrent(EditorDocument model, ManagementApi api, string? cluster, string? peer, int generation)
    {
        if (generation != model.Generation || !string.Equals(cluster, api.SelectedClusterId, StringComparison.Ordinal)
            || !string.Equals(peer, api.SelectedPeerId, StringComparison.Ordinal))
        {
            throw UiError.Attach(new InvalidOperationException("The connection or displayed workflow changed. The delayed document will not be applied."), "The connection or displayed workflow changed. The delayed document will not be applied.");
        }
    }

    /// <summary>画面固有草稿を優先し、なければ同じ接続先の前回保存版を使います。</summary>
    /// <returns>最新状態とは区別した表示説明です。</returns>
    private static async Task<string> OpenPreviousAsync(EditorDocument model, BrowserStorage storage, string? cluster, string? peer, string id, CancellationToken cancellationToken = default)
    {
        if (await model.RestoreRetainedAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return "offline：この画面に保管した草稿・前回保存版を表示しています。接続先の最新状態は未確認です。";
        }
        StoredWorkflow? previous = await EditorSavedCache.ReadAsync(storage, cluster, peer, id, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            throw UiError.Attach(new InvalidOperationException("Offline: no complete workflow document has been retrieved and retained for this connection. It will not be replaced with another or an empty document."), "Offline: no complete workflow document has been retrieved and retained for this connection. It will not be replaced with another or an empty document.");
        }
        await model.LoadAsync(previous, cancellationToken).ConfigureAwait(false);
        return "offline：この接続先から前回取得した保存版を表示しています。接続先の最新状態は未確認です。";
    }
}
