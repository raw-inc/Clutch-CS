namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>元の要求本文を変えず、受信したPCの単調時計で開始期限を判定します。</summary>
public sealed class RequestAdmission
{
    private readonly TimeProvider clock;
    private readonly long receivedAt;
    private readonly TimeSpan remaining;
    private readonly DateTimeOffset expiresAt;

    /// <summary>輸送層で確認した残り時間を固定します。開始済み処理の取消には使いません。</summary>
    public RequestAdmission(TimeProvider clock, TimeSpan remaining, DateTimeOffset expiresAt, DateTimeOffset sourceNow)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        receivedAt = clock.GetTimestamp();
        this.remaining = remaining;
        this.expiresAt = expiresAt;
        SourceNow = sourceNow;
    }

    /// <summary>UUIDの作成時刻と同じ時計で記録した、送信時の日時です。</summary>
    public DateTimeOffset SourceNow
    {
        get;
    }

    /// <summary>新しい開始を受け付けられる時間です。受信後の日時補正には左右されません。</summary>
    public TimeSpan Remaining => remaining - clock.GetElapsedTime(receivedAt);

    /// <summary>未開始要求の元の受付期間が終わったかです。</summary>
    public bool IsExpired => Remaining <= TimeSpan.Zero;

    /// <summary>同じ送信元時計の内側要求へ、消費済みの時間を引き継ぎます。</summary>
    /// <returns>受付期間を取り直さない内側要求の期限です。</returns>
    public RequestAdmission ForDeadline(DateTimeOffset deadline) => new(clock, Remaining + (deadline - expiresAt), deadline, SourceNow);
}
