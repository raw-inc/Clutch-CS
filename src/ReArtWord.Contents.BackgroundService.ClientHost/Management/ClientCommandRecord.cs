namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>秘密を含む要求本文を保存せず、元の実体への操作と照会を固定します。</summary>
/// <param name="RequestId">外側の永続受付ID。</param>
/// <param name="ClusterId">元のクラスタ。</param>
/// <param name="TargetPeerId">元の論理Peer。</param>
/// <param name="TargetInstanceId">開始を許す元の実体。</param>
/// <param name="ExpiresAt">元の未開始受付期限。</param>
/// <param name="Operation">登録済みの操作名。</param>
/// <param name="PayloadHash">秘密本文を含めない要求内容の指紋。</param>
/// <param name="RequestFingerprint">元のクラスタ要求全体の照会用指紋。</param>
internal sealed record ClientCommandRecord(Guid RequestId, string ClusterId, string TargetPeerId, Guid TargetInstanceId, DateTimeOffset ExpiresAt, string Operation, string PayloadHash, string RequestFingerprint);
