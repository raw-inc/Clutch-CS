namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>保持境界だけを進める試験用時刻です。</summary>
internal sealed class LogTestClock : TimeProvider
{
    private readonly Lock gate = new();
    private DateTimeOffset now = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    /// <summary>現在の試験時刻を返します。</summary>
    /// <returns>固定したUTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow()
    {
        using (gate.EnterScope())
        {
            return now;
        }
    }

    /// <summary>次に開始する保持処理の時刻を進めます。</summary>
    internal void Advance(TimeSpan duration)
    {
        using (gate.EnterScope())
        {
            now += duration;
        }
    }
}
