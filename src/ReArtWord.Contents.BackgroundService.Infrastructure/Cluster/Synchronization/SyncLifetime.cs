namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>転送・取込み・結果保持が共有する元の残り時間を、ローカルの単調時計で消費します。</summary>
internal sealed class SyncLifetime(TimeProvider clock, TimeSpan duration)
{
    private readonly long started = clock.GetTimestamp();

    /// <summary>受入時に固定した上限です。</summary>
    internal TimeSpan Duration => duration;

    /// <summary>待機・転送・保存準備を含む残り時間です。</summary>
    internal TimeSpan Remaining => duration - clock.GetElapsedTime(started);

    /// <summary>返送側の上限が短い場合だけ短縮し、元の残り時間は延ばしません。</summary>
    /// <returns>元の期限、または短縮後の期限です。</returns>
    internal SyncLifetime Limit(TimeSpan maximum) => maximum < Remaining ? new(clock, maximum)
                                                                         : this;

    /// <summary>旧形式も受入後は日時補正によって延命しません。</summary>
    internal bool Expired => Remaining <= TimeSpan.Zero;
}
