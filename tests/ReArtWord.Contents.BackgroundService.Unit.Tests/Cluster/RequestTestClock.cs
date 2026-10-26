namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>要求の開始を確認してから期限を進め、試験環境の処理速度に依存させません。</summary>
internal sealed class RequestTestClock : TimeProvider
{
    private readonly DateTimeOffset origin = DateTimeOffset.UtcNow;
    private long elapsed;

    /// <summary>受付期限とPeerの観測に同じ時刻を返します。</summary>
    /// <returns>試験が明示的に進めたUTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(Interlocked.Read(ref elapsed));

    /// <summary>実時間を待たずに受付期限を超えます。</summary>
    internal void Advance(TimeSpan amount) => Interlocked.Add(ref elapsed, amount.Ticks);
}
