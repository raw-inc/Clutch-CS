namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>要求を受け付けられる残り時間を、受信側PCの単調時計で管理します。</summary>
public sealed class RequestAdmission
{
    private readonly TimeProvider clock;
    private readonly long receivedAt;
    private readonly TimeSpan remaining;
    private readonly DateTimeOffset expiresAt;

    /// <summary>通信処理で換算した残り時間を記録します。この期限は実行開始の可否にだけ使い、開始済みの処理は取り消しません。</summary>
    public RequestAdmission(TimeProvider clock, TimeSpan remaining, DateTimeOffset expiresAt, DateTimeOffset sourceNow)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        receivedAt = clock.GetTimestamp();
        this.remaining = remaining;
        this.expiresAt = expiresAt;
        SourceNow = sourceNow;
    }

    /// <summary>新規要求の受付期間を発行時点から計測します。保存済み要求の復元には使用しません。</summary>
    /// <returns>表示用の期限と、時刻補正で増減しない残り時間を持つ受付情報です。</returns>
    public static RequestAdmission Start(TimeProvider clock, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        DateTimeOffset issuedAt = clock.GetUtcNow();
        return new(clock, lifetime, issuedAt + lifetime, issuedAt);
    }

    /// <summary>発行時の時計で表した期限です。本文とIDの時刻を揃えるために使用し、残り時間の計測には使用しません。</summary>
    public DateTimeOffset ExpiresAt => expiresAt;

    /// <summary>要求元の時刻系で表した、期限換算時の基準日時です。UUIDv7に含まれる生成日時の検証にも使います。</summary>
    public DateTimeOffset SourceNow
    {
        get;
    }

    /// <summary>実行開始を受け付けられる残り時間です。受信後の経過時間を単調時計で差し引くため、OSの時刻補正には影響されません。</summary>
    public TimeSpan Remaining => remaining - clock.GetElapsedTime(receivedAt);

    /// <summary>実行開始の受付期限を過ぎているかどうかです。</summary>
    public bool IsExpired => Remaining <= TimeSpan.Zero;

    /// <summary>要求本文に含まれる別の要求へ、通信中に経過した時間を差し引いた受付期限を引き継ぎます。</summary>
    /// <returns>元の期限との差を残り時間に加えた受付情報です。deadlineは元の期限と同じPCの時計で表す必要があります。</returns>
    public RequestAdmission ForDeadline(DateTimeOffset deadline) => new(clock, Remaining + (deadline - expiresAt), deadline, SourceNow);
}
