using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>保存済みの操作段階と、利用者の確認が必要な結果です。</summary>
/// <param name="Id">グループIDです。</param>
/// <param name="OperationId">一回のグループ操作を識別するIDです。</param>
/// <param name="Phase">現在の操作段階です。</param>
/// <param name="ApplicationId">現在操作しているアプリケーションIDです。</param>
/// <param name="NextAt">次の起動を開始できる時刻です。</param>
/// <param name="Outcome">確定した操作結果です。進行中はnullです。</param>
/// <param name="Reason">保留や失敗を区別する理由です。</param>
/// <param name="RecoverySuppressed">グループの明示停止により自動復旧を抑止しているかです。</param>
public sealed record ProcessGroupStatus(string Id, Guid? OperationId, EProcessGroupPhase Phase, string? ApplicationId = null,
    DateTimeOffset? NextAt = null, EOutcome? Outcome = null, string? Reason = null, bool RecoverySuppressed = false);
