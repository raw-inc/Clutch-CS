using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>確定保存と独立した下書きの確認・取消境界です。</summary>
public interface ISnapshotEditor
{
    /// <summary>選択範囲の未保存状態を確認します。</summary>
    /// <returns>現在の下書きと編集者の状態です。</returns>
    public Task<SnapshotEditorState> InspectAsync(SnapshotEditLock scope, CancellationToken cancellationToken = default);

    /// <summary>人が確認した元範囲だけを保存済み確認または取消します。</summary>
    /// <returns>調停結果です。</returns>
    public Task<ConfigurationPhaseResult> ResolveAsync(SnapshotDraftResolution request, SnapshotEditLock scope, CancellationToken cancellationToken = default);
}
