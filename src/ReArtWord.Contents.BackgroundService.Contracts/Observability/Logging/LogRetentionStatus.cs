namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>一Peerで確認できた保持方針の反映と保留状態です。</summary>
/// <param name="AppliedHash">保存済み反映記録の方針ハッシュ。</param>
/// <param name="Applied">現在確認できたローカル保持方針。</param>
/// <param name="Problem">保存・適用を確認できない理由。</param>
/// <param name="MaintenanceHeld">削除・圧縮の自動処理を保留しているか。</param>
public sealed record LogRetentionStatus(string? AppliedHash, LogRetentionSettings? Applied, string? Problem, bool MaintenanceHeld);
