using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Runtime.Resources;

/// <summary>ワークフロー未設定でも動く共通リソース観測と履歴境界です。</summary>
public interface IResourceService
{
    /// <summary>現在の実測と取得状態を取得して診断履歴へ保存します。</summary>
    /// <returns>履歴の欠損状態も含む実測サンプルです。</returns>
    public ValueTask<ResourceSample> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>このPeerで保存された共通診断履歴を取得します。</summary>
    /// <returns>時刻順の診断履歴です。</returns>
    public ValueTask<IReadOnlyList<ResourceSample>> HistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Hostの寿命中、ワークフローから独立して共通履歴を収集します。</summary>
    /// <returns>Host終了までの収集処理です。</returns>
    public Task RunAsync(CancellationToken cancellationToken = default);
}
