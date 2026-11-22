namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>履歴整理の基準日時を、時計補正やPeerの離脱だけで先へ進めません。</summary>
public sealed class HistoryRetentionClock
{
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private readonly Guid origin;
    private readonly long started;
    private DateTimeOffset epoch;
    private HistoryTimeReference? selected;
    private long observed;

    /// <summary>この起動実体のUTCと単調時計を対応付けます。</summary>
    public HistoryRetentionClock(TimeProvider clock, Guid? originId = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        origin = originId ?? Guid.NewGuid();
        if (origin == Guid.Empty)
        {
            throw new ArgumentException("時刻基準の発行元実体が必要です。", nameof(originId));
        }
        started = observed = clock.GetTimestamp();
        epoch = clock.GetUtcNow();
    }

    /// <summary>保持期間の計算に使う、保守的な基準日時です。</summary>
    /// <returns>実際の経過時間を超えて先へ進めていない日時です。</returns>
    public DateTimeOffset Capture() => Sample().Time;

    /// <summary>基準の識別も渡し、同じ日時をPeer間で循環させて遅らせ続けることを防ぎます。</summary>
    /// <returns>元の基準と、それからの経過時間です。</returns>
    public HistoryTimeReference Sample()
    {
        using (gate.EnterScope())
        {
            return Advance();
        }
    }

    /// <summary>より古い基準を引き継ぎます。元のPeerが離れても、以後は手元の経過時間で進めます。</summary>
    public void Observe(HistoryTimeReference other, bool fromOrigin = false)
    {
        ArgumentNullException.ThrowIfNull(other);
        other.Validate();
        using (gate.EnterScope())
        {
            HistoryTimeReference current = Advance();
            if (other.OriginId == current.OriginId)
            {
                // 同じ基準の再受信では経過時間を戻しません。往復のたびに通信時間を
                // 差し引くと、基準日時が徐々に遅れ、正常な時計でも整理が進まなくなるためです。
                // 元の時計が戻った場合だけ、既に計測中の経過時間を保って起点を補正します。
                if (other.Epoch < current.Epoch)
                {
                    selected = current = current with { Epoch = other.Epoch };
                }
                if (fromOrigin && other.ElapsedTicks < current.ElapsedTicks)
                {
                    // 発行元から直接届いた連番付き通知だけは、手元の計測が先行していた分を補正します。
                    // 中継先の計測を繰り返し採用して通信遅延を積み増すことはありません。
                    selected = current with { ElapsedTicks = other.ElapsedTicks };
                }
            }
            else if (other.Time < current.Time)
            {
                selected = other;
            }
        }
    }

    /// <summary>ローカルUTCの前進は採用せず、後退時は新しい記録より先へ整理を進めないようにします。</summary>
    /// <returns>現在まで進めた、選択中の基準です。</returns>
    private HistoryTimeReference Advance()
    {
        long current = clock.GetTimestamp();
        TimeSpan elapsed = clock.GetElapsedTime(started, current);
        DateTimeOffset adjusted = clock.GetUtcNow() - elapsed;
        if (adjusted < epoch)
        {
            epoch = adjusted;
        }
        HistoryTimeReference own = new(origin, epoch, elapsed.Ticks);
        if (selected is not null)
        {
            selected = selected with { ElapsedTicks = checked(selected.ElapsedTicks + clock.GetElapsedTime(observed, current).Ticks) };
        }
        if (selected is null || own.Time < selected.Time)
        {
            selected = own;
        }
        observed = current;
        return selected;
    }
}
