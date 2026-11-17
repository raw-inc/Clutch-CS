using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>計測値の鮮度を、PC間の時計差と受信側の時刻補正に影響されずに評価するための記録です。</summary>
internal sealed record ResourceSelectionObservation(ResourceSample Sample, long RequestedAt, TimeSpan SourceAge)
{
    /// <summary>採取元で計算した標本の古さに、要求開始からの経過時間を加えて、受信側の日時へ換算します。</summary>
    /// <returns>鮮度比較用にObservedAtを換算した標本です。元の標本と保存履歴は変更しません。</returns>
    // 受信時から数えると、通信に時間がかかった標本を新しいと誤判定します。
    // RequestedAtを起点に往復の通信時間も加算し、実際より新しい値として選ばれないようにします。
    internal ResourceSample Capture(TimeProvider clock)
        => Sample with { ObservedAt = clock.GetUtcNow() - SourceAge - clock.GetElapsedTime(RequestedAt) };
}
