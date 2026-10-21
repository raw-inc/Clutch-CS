namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>端末セッション一覧と入力確認に使用する状態です。</summary>
/// <param name="SessionId">セッションIDです。</param>
/// <param name="Shell">実際に起動したシェルです。</param>
/// <param name="Owner">現在の入力所有者です。</param>
/// <param name="Sequence">次に受付可能な入力連番です。</param>
/// <param name="StartedAt">開始時刻です。</param>
/// <param name="Exited">プロセスの終了を確認したかです。</param>
/// <param name="Elevated">管理者権限で起動したセッションかです。</param>
/// <param name="System">未ログオン時にSYSTEMとして起動したかです。</param>
public sealed record TerminalView(Guid SessionId, string Shell, Guid Owner, long Sequence, DateTimeOffset StartedAt, bool Exited, bool Elevated = false, bool System = false);
