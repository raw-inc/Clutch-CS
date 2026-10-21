using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>マニフェストの全ファイル一覧を定期再配信しない更新進捗です。</summary>
/// <param name="OperationId">元要求の識別子です。</param>
/// <param name="ReleaseId">対象リリースです。</param>
/// <param name="MaximumAttempts">初回を含む試行上限です。</param>
/// <param name="Completed">自動処理が終了したかです。</param>
/// <param name="Outcome">確認済みの全体結果です。</param>
/// <param name="Peers">対象別の進捗です。</param>
/// <param name="UpdatedAt">保存時刻です。</param>
/// <param name="Reason">停止理由です。</param>
/// <param name="Client">ClientHost自身の更新状態です。</param>
/// <param name="MaximumParallelism">開始時に固定した同時更新上限です。</param>
public sealed record FleetUpdateView(Guid OperationId, string ReleaseId, int MaximumAttempts, bool Completed, EOutcome Outcome,
    IReadOnlyList<FleetUpdatePeerProgress> Peers, DateTimeOffset UpdatedAt, string? Reason = null, ClientUpdateProgress? Client = null, int MaximumParallelism = 2)
{
    /// <summary>マニフェストや元配布パスを含めず、所有者用の表示へ変換します。</summary>
    /// <returns>既存の結果照会と同じ固定操作の表示です。</returns>
    public static FleetUpdateView From(FleetUpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new(status.Request.OperationId, status.Request.Release.Manifest.ReleaseId, status.Request.Release.MaximumAttempts,
            status.Completed, status.Outcome, status.Peers, status.UpdatedAt, status.Reason, status.Client, status.Request.Release.MaximumParallelism);
    }
}
