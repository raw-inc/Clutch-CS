namespace ReArtWord.Contents.BackgroundService.Infrastructure.Resources.Counters;

/// <summary>差分に必要な前回未加工のサンプルと照会寿命を保持します。</summary>
internal interface IPerformanceCounterQuery : IDisposable
{
    /// <summary>一巡を採取し、取得不能をnullで返します。</summary>
    /// <returns>状態付きのカウンター実体です。</returns>
    public IReadOnlyList<PerformanceCounterValue>? Collect();
}
