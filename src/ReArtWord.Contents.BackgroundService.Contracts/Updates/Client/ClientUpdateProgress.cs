using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;

/// <summary>ClientHostの停止をまたいで独立Updaterが保存する実確認結果です。</summary>
/// <param name="State">処理段階です。</param>
/// <param name="Completed">自動処理が終わったかです。</param>
/// <param name="Outcome">確認済み結果です。</param>
/// <param name="Worker">この一回を所有するUpdater実体です。</param>
/// <param name="UpdatedAt">記録時刻です。</param>
/// <param name="Reason">失敗または未確認の理由です。</param>
public sealed record ClientUpdateProgress(string State, bool Completed, EOutcome Outcome, ProcessIdentity? Worker,
    DateTimeOffset UpdatedAt, string? Reason = null);
