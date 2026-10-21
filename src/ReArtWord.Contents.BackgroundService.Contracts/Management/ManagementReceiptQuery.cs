namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>秘密ペイロードを保存せず元管理要求の操作記録だけを照会する条件です。</summary>
/// <param name="RequestId">元の外側要求ID。</param>
/// <param name="ClusterId">元のクラスタ。</param>
/// <param name="TargetPeerId">元の論理Peer。</param>
/// <param name="Fingerprint">元ClusterRequest全体の内容指紋。</param>
public sealed record ManagementReceiptQuery(Guid RequestId, string ClusterId, string TargetPeerId, string Fingerprint);
