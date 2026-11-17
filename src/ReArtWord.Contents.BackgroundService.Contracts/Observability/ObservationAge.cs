namespace ReArtWord.Contents.BackgroundService.Contracts.Observability;

/// <summary>採取・受信からの経過時間を保持し、PCの日時補正を標本の鮮度へ持ち込みません。</summary>
public readonly record struct ObservationAge
{
    private readonly TimeProvider? clock;
    private readonly long started;
    private readonly TimeSpan initial;

    /// <summary>採取元で判明した古さを引き継ぎ、その後の経過時間を計測します。</summary>
    public ObservationAge(TimeProvider clock, TimeSpan initial = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(initial, TimeSpan.Zero);
        this.clock = clock;
        this.initial = initial;
        started = clock.GetTimestamp();
    }

    /// <summary>一件の選択計画では、全ノードで同じ評価結果を使います。</summary>
    private ObservationAge(TimeSpan value)
    {
        initial = value;
        clock = null;
        started = 0;
    }

    /// <summary>元の採取からの経過時間です。</summary>
    public TimeSpan Elapsed => initial + (clock?.GetElapsedTime(started) ?? TimeSpan.Zero);

    /// <summary>同じ計画の途中で標本の期限判定が変わらないよう、現在の古さを固定します。</summary>
    /// <returns>以降は時間が進まない、選択時点の値です。</returns>
    public ObservationAge Freeze() => new(Elapsed);
}
