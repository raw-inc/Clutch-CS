namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>編集UIの頻度と保持期限を実時間待ちなしで確認します。</summary>
internal sealed class EditorTestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private long elapsed;

    /// <summary>日時と同じ時間だけ進む試験用の単調時計です。</summary>
    /// <returns>開始からの経過tick数です。</returns>
    public override long GetTimestamp() => elapsed;

    /// <summary>TimeSpanと同じ単位で進めます。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>現在の試験時刻です。</summary>
    /// <returns>UTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>試験の経過時間を進めます。</summary>
    internal void Advance(TimeSpan duration)
    {
        now += duration;
        elapsed += duration.Ticks;
    }
}
