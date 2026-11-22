using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>計測値の鮮度を、PC間の時計差と受信側の時刻補正に影響されずに評価するための記録です。</summary>
internal sealed record ResourceSelectionObservation(ResourceSample Sample, ObservationAge Age)
{
    /// <summary>採取元で計算した標本の古さに、要求開始からの経過時間を加えて、受信側の日時へ換算します。</summary>
    /// <returns>鮮度比較用にObservedAtを換算した標本です。元の標本と保存履歴は変更しません。</returns>
    // 受信時から数えると、通信に時間がかかった標本を新しいと誤判定します。
    // 通信の往復時間は受信時にAgeへ加算済みです。UTCへの換算は表示用で、鮮度の判定にはAgeを使います。
    internal ResourceSample Capture(TimeProvider clock)
        => Sample with { ObservedAt = clock.GetUtcNow() - Age.Elapsed, Age = Age };
}
