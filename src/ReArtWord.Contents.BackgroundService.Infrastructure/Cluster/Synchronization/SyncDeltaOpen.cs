namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>一つの照合で固定した両方向の差分を結び付けます。</summary>
/// <param name="Manifest">今回受け取る相手の差分です。</param>
/// <param name="ReverseId">自分が先に作成した、同じ相手向けの逆方向差分です。</param>
internal sealed record SyncDeltaOpen(SyncManifest Manifest, Guid ReverseId);
