namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;

/// <summary>一つの確定履歴の元JSONと、取得できなかった理由を分けます。</summary>
/// <param name="Reference">要求された元の識別情報です。</param>
/// <param name="Payload">元ハッシュと一致する保存本文です。</param>
/// <param name="Problem">未取得理由です。未保存や成功への読み替えには使用しません。</param>
public sealed record RecoveryRecordBody(RecoveryRecord Reference, string? Payload, string? Problem = null);
