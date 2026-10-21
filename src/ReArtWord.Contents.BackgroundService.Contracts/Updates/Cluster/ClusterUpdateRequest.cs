using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;

/// <summary>人が開始前に固定するクラスタ全体の更新入力です。</summary>
/// <param name="OperationId">一回の更新識別子です。</param>
/// <param name="Manifest">一つのリリース契約です。</param>
/// <param name="PayloadRoot">起点Peerの明示ペイロードです。</param>
/// <param name="PeerIds">開始時に固定する全対象Peerです。</param>
/// <param name="FailurePolicy">更新失敗時の事前方針です。</param>
/// <param name="RollbackFailurePolicy">復元失敗時の事前方針です。</param>
/// <param name="AllowWhileActive">稼働中更新を明示許可します。</param>
/// <param name="RemainingOperationConfirmed">残存側の通常処理維持を利用者が確認しました。</param>
/// <param name="PeerTimeoutMilliseconds">各Peerの更新結果を待つ有限上限です。</param>
/// <param name="Variants">指定時は実PeerのRIDで選ぶ同一リリースの成果物一覧です。</param>
public sealed record ClusterUpdateRequest(Guid OperationId, ReleaseManifest Manifest, string PayloadRoot, IReadOnlyList<string> PeerIds, EUpdateFailurePolicy FailurePolicy, EUpdateRollbackFailurePolicy RollbackFailurePolicy = EUpdateRollbackFailurePolicy.ExcludeFailed, bool AllowWhileActive = false, bool RemainingOperationConfirmed = false, int PeerTimeoutMilliseconds = 600000, IReadOnlyList<ReleaseVariant>? Variants = null);
