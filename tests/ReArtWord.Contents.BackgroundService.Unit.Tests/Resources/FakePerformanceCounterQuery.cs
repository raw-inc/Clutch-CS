using ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>native値の初回・取得不能・回復を制御する観測境界です。</summary>
internal sealed class FakePerformanceCounterQuery(params IReadOnlyList<PerformanceCounterValue>?[] samples) : IPerformanceCounterQuery
{
    private int index;

    /// <summary>所有者が照会を解放したかです。</summary>
    internal bool Disposed
    {
        get; private set;
    }

    /// <summary>次の生測定結果を返します。</summary>
    /// <returns>取得不能ならnullです。</returns>
    public IReadOnlyList<PerformanceCounterValue>? Collect()
    {
        int selected = Math.Min(index++, samples.Length - 1);
        return samples[selected];
    }

    /// <summary>観測器が所有した寿命の終了を記録します。</summary>
    public void Dispose() => Disposed = true;
}
