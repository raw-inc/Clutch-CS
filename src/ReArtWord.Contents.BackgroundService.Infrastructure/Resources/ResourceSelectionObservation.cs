using ReArtWord.Contents.BackgroundService.Contracts.Resources;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources;

/// <summary>時計補正で観測が若返らないよう、受信後の経過時間を単調時計で保持します。</summary>
internal sealed record ResourceSelectionObservation(ResourceSample Sample, long RequestedAt, TimeSpan SourceAge)
{
    /// <summary>比較時のPCの日時へ換算します。採取元の標本や保存履歴は変更しません。</summary>
    /// <returns>通信待ち時間も含めた鮮度を持つ比較用の標本です。</returns>
    internal ResourceSample Capture(TimeProvider clock)
        => Sample with { ObservedAt = clock.GetUtcNow() - SourceAge - clock.GetElapsedTime(RequestedAt) };
}
