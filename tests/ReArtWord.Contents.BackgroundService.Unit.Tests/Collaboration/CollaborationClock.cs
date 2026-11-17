namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Collaboration;

/// <summary>表示保持期限を実時間待ちなしで確認します。</summary>
internal sealed class CollaborationClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private long ticks;

    /// <summary>TimeSpanと同じ単位で経過時間を数えます。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>日時とは独立した試験用の経過時間です。</summary>
    /// <returns>経過tick数です。</returns>
    public override long GetTimestamp() => ticks;

    /// <summary>経過時間を進めず、日時補正だけを模擬します。</summary>
    internal void Adjust(TimeSpan offset) => now += offset;

    /// <summary>現在の試験時刻です。</summary>
    /// <returns>UTC時刻。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>期限を進めます。</summary>
    public void Advance(TimeSpan elapsed)
    {
        now += elapsed;
        ticks += elapsed.Ticks;
    }
}
