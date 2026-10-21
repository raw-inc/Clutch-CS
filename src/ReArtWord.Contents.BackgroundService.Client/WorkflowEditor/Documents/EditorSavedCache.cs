using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>実取得した完全保存版を、草稿とは別の接続先領域に保持します。</summary>
internal static class EditorSavedCache
{
    /// <summary>未取得の定義を補完せず前回の保存版を読みます。</summary>
    /// <returns>同じ接続先で取得した保存版か未取得です。</returns>
    internal static async Task<StoredWorkflow?> ReadAsync(BrowserStorage storage, string? cluster, string? peer, string id, CancellationToken cancellationToken = default)
    {
        try
        {
            StoredWorkflow? saved = await storage.ReadAsync<StoredWorkflow>(Key(cluster, peer, id), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (saved is not null && string.Equals(saved.Definition.Id, id, StringComparison.Ordinal))
            {
                return saved;
            }
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>検査済みの保存版だけを保持します。</summary>
    /// <returns>端末へ保存できたか。</returns>
    internal static async Task<bool> WriteAsync(BrowserStorage storage, string? cluster, string? peer, StoredWorkflow saved, CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.WriteAsync(Key(cluster, peer, saved.Definition.Id), saved, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <summary>確認済み削除をこの接続先の保存済みキャッシュへ反映します。</summary>
    /// <returns>当該キャッシュの解除です。</returns>
    internal static ValueTask ForgetAsync(BrowserStorage storage, string? cluster, string? peer, string id, CancellationToken cancellationToken = default)
        => storage.WriteAsync<StoredWorkflow?>(Key(cluster, peer, id), null, cancellationToken: cancellationToken);

    /// <summary>クラスタ・Peer・ワークフローを混ぜない保存キーです。</summary>
    /// <returns>保存先キーです。</returns>
    private static string Key(string? cluster, string? peer, string id) => "contents.editor.saved." + JsonSerializer.Serialize(new[] { cluster, peer, id });
}
