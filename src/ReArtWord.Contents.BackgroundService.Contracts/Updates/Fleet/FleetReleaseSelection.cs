using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>ClientHost上の配布元と、host単位更新の事前方針です。</summary>
/// <param name="Manifest">単一RIDまたは代表マニフェストです。</param>
/// <param name="PayloadRoot">ClientHost上の絶対配布元です。</param>
/// <param name="AllowWhileActive">稼働中の更新を明示許可します。</param>
/// <param name="MaximumAttempts">初回を含む最大試行回数です。</param>
/// <param name="PeerTimeoutMilliseconds">一ピアの復帰確認期限です。</param>
/// <param name="Variants">RID別配布がある場合の一覧です。</param>
/// <param name="Client">全Core成功後に同じインストール先へ適用するClientHost配布です。</param>
/// <param name="MaximumParallelism">同時更新の上限です。1から3、既定は2で、安全条件により減少します。</param>
public sealed record FleetReleaseSelection(ReleaseManifest Manifest, string PayloadRoot, bool AllowWhileActive = false,
    int MaximumAttempts = 3, int PeerTimeoutMilliseconds = 600000, IReadOnlyList<ReleaseVariant>? Variants = null, ReleaseVariant? Client = null, int MaximumParallelism = 1);
