namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>固定対象の進捗と、最大回数に制限した試行履歴です。</summary>
/// <param name="Target">更新対象です。</param>
/// <param name="State">待機・準備・復帰確認・保留などの段階です。</param>
/// <param name="Attempts">初回から順に保持する操作IDです。</param>
/// <param name="Reason">保留・失敗理由です。</param>
public sealed record FleetUpdatePeerProgress(FleetUpdateTarget Target, string State, IReadOnlyList<FleetUpdateAttempt> Attempts, string? Reason = null);

