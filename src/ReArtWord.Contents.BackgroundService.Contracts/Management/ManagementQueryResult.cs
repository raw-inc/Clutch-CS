namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>元要求の操作記録を読み取り、実行中と結果不明を区別します。</summary>
/// <param name="Running">同じ元の実体で処理が継続中か。</param>
/// <param name="Reply">確認した元結果。</param>
/// <param name="Reason">照会不能・元の実体喪失等の理由。</param>
public sealed record ManagementQueryResult(bool Running, ManagementReply? Reply = null, string? Reason = null);
