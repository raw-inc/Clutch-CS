using System.Text.Json;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;

/// <summary>オフライン編集の補助に限って前回取得したカタログと保存一覧を保持します。</summary>
internal sealed record EditorReferenceData(IReadOnlyList<NodeDescriptor> Catalog, IReadOnlyList<WorkflowListSummary> Workflows)
{
    /// <summary>明示クラスタ・Peerの前回取得データを読みます。</summary>
    /// <returns>保存済みデータか未保存です。</returns>
    internal static async Task<EditorReferenceData?> ReadAsync(BrowserStorage storage, string? clusterId, string? peerId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await storage.ReadAsync<EditorReferenceData>(Key(clusterId, peerId), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JsonException)
        {
            return null;
        }
    }

    /// <summary>取得成功した補助情報を保存します。失敗しても取得結果は維持します。</summary>
    /// <returns>保存の確認結果です。</returns>
    internal async Task<bool> WriteAsync(BrowserStorage storage, string? clusterId, string? peerId, CancellationToken cancellationToken = default)
    {
        try
        {
            await storage.WriteAsync(Key(clusterId, peerId), this, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <summary>異なる接続先のカタログを混ぜない保存キーです。</summary>
    /// <returns>接続先別のキーです。</returns>
    private static string Key(string? clusterId, string? peerId) => "contents.editor.references." + JsonSerializer.Serialize(new[] { clusterId, peerId });
}
