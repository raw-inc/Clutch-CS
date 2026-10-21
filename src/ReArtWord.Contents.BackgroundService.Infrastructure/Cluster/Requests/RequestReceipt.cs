using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>開始済み処理を再実行しないための永続記録です。</summary>
/// <param name="Fingerprint">要求内容。</param>
/// <param name="InstanceId">受理した実体。</param>
/// <param name="ExpiresAt">最初の開始期限。</param>
/// <param name="Response">完了結果。nullなら開始済みで結果未確認。</param>
internal sealed record RequestReceipt(string Fingerprint, Guid InstanceId, DateTimeOffset ExpiresAt, ClusterResponse? Response);
