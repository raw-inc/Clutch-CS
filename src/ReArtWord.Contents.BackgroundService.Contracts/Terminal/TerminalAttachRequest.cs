namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>既存PTYの所有権を変更せず、有限寿命の接続許可を要求します。</summary>
/// <param name="SessionId">既存セッションです。</param>
/// <param name="Owner">確定済みの入力所有者です。</param>
/// <param name="ConnectionId">同じ開始要求の重複を防ぐ識別子です。</param>
public sealed record TerminalAttachRequest(Guid SessionId, Guid Owner, Guid ConnectionId);
