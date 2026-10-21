using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>監視処理から選択クラスタの元Peer実体へ送る管理操作です。</summary>
/// <param name="RequestId">再送・照会を通じて固定する外側要求ID。</param>
/// <param name="ClusterId">明示したクラスタ。</param>
/// <param name="TargetPeerId">明示した論理Peer。</param>
/// <param name="TargetInstanceId">新規実行を許す元の実体。</param>
/// <param name="ExpiresAt">未開始受理の期限。開始済み処理の停止期限ではありません。</param>
/// <param name="Operation">登録済み管理操作名。</param>
/// <param name="Payload">ドメイン operationIdを含む元の引数。</param>
public sealed record ManagementCommand(Guid RequestId, string ClusterId, string TargetPeerId, Guid TargetInstanceId, DateTimeOffset ExpiresAt, string Operation, NodeValue? Payload);
