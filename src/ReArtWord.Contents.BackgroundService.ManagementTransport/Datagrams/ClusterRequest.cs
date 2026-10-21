using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>明示対象へ配送する種類付きの管理要求です。</summary>
/// <param name="Id">再送を通じて一定の操作ID。</param>
/// <param name="Kind">登録済み処理種類。</param>
/// <param name="TargetPeerId">対象Peer。</param>
/// <param name="TargetInstanceId">対象起動実体。</param>
/// <param name="ExpiresAt">新規開始できる期限。</param>
/// <param name="Payload">処理固有の入力。</param>
public sealed record ClusterRequest(Guid Id, string Kind, string TargetPeerId, Guid TargetInstanceId, DateTimeOffset ExpiresAt, NodeValue Payload);
