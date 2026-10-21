namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>反復の開始時刻と待機タイマーを同じ単調時計で制御します。</summary>
internal sealed class IterationTestClock : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ClockTimer> timers = [];
    private readonly DateTimeOffset origin = DateTimeOffset.UtcNow;
    private long ticks;

    /// <summary>経過時間をTimeSpanと同じ単位で表します。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>次の開始を待つタイマー数です。</summary>
    internal int Pending
    {
        get
        {
            using (gate.EnterScope())
            {
                return timers.Count(timer => timer.Due != long.MaxValue);
            }
        }
    }

    /// <summary>実時間に左右されない経過時刻です。</summary>
    /// <returns>試験内の単調時刻です。</returns>
    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    /// <summary>実行要求の受付期限にも同じ経過時間を使用します。</summary>
    /// <returns>試験内のUTC時刻です。</returns>
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(GetTimestamp());

    /// <summary>Task.Delayが登録した待ち時間を、時刻を進めたときだけ通知します。</summary>
    /// <returns>取消可能な試験タイマーです。</returns>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ClockTimer timer = new(this, callback, state);
        using (gate.EnterScope())
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    /// <summary>指定ミリ秒だけ進め、期限を迎えた待機を通知します。</summary>
    internal void Advance(int milliseconds)
    {
        ClockTimer[] due;
        using (gate.EnterScope())
        {
            ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            due = timers.Where(timer => timer.Due <= ticks).ToArray();
            foreach (ClockTimer timer in due)
            {
                timer.Due = long.MaxValue;
            }
        }
        foreach (ClockTimer timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>一回の待ち時間だけを登録・解除します。</summary>
    private sealed class ClockTimer(IterationTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due
        {
            get; set;
        } = long.MaxValue;

        /// <summary>期限到達を一度通知します。</summary>
        internal void Fire() => callback(state);

        /// <summary>今回の待ち時間を登録します。</summary>
        /// <returns>登録できた場合はtrueです。</returns>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            using (owner.gate.EnterScope())
            {
                if (!owner.timers.Contains(this))
                {
                    return false;
                }
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("The fixture uses one-shot delays only.");
                }
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue
                                                          : owner.ticks + dueTime.Ticks;
                return true;
            }
        }

        /// <summary>取消後にコールバックを通知しません。</summary>
        public void Dispose()
        {
            using (owner.gate.EnterScope())
            {
                owner.timers.Remove(this);
            }
        }

        /// <summary>非同期破棄でも同じ登録を除去します。</summary>
        /// <returns>解除完了です。</returns>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
