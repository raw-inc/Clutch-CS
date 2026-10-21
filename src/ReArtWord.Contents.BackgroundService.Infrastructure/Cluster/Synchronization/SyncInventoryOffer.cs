namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>照合した元の全体指紋と、その相手へ渡す固定差分です。</summary>
/// <param name="Hashes">区分ごとの元の全体指紋です。</param>
/// <param name="Manifest">元相手・実体・期限を固定した差分の転送契約です。</param>
/// <param name="Full">保持区間の重なりがないため、現在基準点から全体を確認するかです。</param>
internal sealed record SyncInventoryOffer(IReadOnlyList<string> Hashes, SyncManifest Manifest, bool Full = false);
