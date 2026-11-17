using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>一つの起動実体が受け付ける期限を、単調時計へ固定します。</summary>
public sealed class RequestAdmissionClock(Guid instanceId, TimeProvider clock)
{
    private readonly long startedAt = clock.GetTimestamp();

    /// <summary>要求元が往復時間を含めて開始期限を換算するための値です。</summary>
    /// <returns>この実体の起動からの経過tick数です。</returns>
    public RequestClockSnapshot Capture() => new(instanceId, clock.GetElapsedTime(startedAt).Ticks);

    /// <summary>再起動前の時計や、元の残り時間を超える期限を受け付けません。</summary>
    /// <returns>本文と保存済み指紋を変更しない、ローカル受付情報付きの要求です。</returns>
    public ClusterRequest Accept(ClockedClusterRequest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ClusterRequest request = envelope.Request ?? throw new InvalidDataException("The original request is missing.");
        return request with { Admission = Create(envelope.ClockInstanceId, envelope.StartByTicks, envelope.SourceNow, request.TargetInstanceId, request.ExpiresAt) };
    }

    /// <summary>ファイル断片の本文を再変換せず、接続で合意した時計の期限を適用します。</summary>
    /// <returns>本文と識別子を維持した管理操作です。</returns>
    public ManagementCommand Accept(ManagementCommand command, RequestClockStamp timing)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(timing);
        return command with { Admission = Create(timing.InstanceId, timing.StartByTicks, timing.SourceNow, command.TargetInstanceId, command.ExpiresAt) };
    }

    /// <summary>本文の種類によらず、元実体と残り期間の検証を共通に行います。</summary>
    /// <returns>現在の単調時計へ固定した受付情報です。</returns>
    private RequestAdmission Create(Guid clockInstanceId, long startByTicks, DateTimeOffset sourceNow, Guid targetInstanceId, DateTimeOffset expiresAt)
    {
        if (clockInstanceId != instanceId || targetInstanceId != instanceId)
        {
            throw new InvalidDataException("The request clock belongs to another instance.");
        }
        long elapsed = clock.GetElapsedTime(startedAt).Ticks;
        TimeSpan remaining = TimeSpan.FromTicks(checked(startByTicks - elapsed));
        TimeSpan sourceRemaining = expiresAt - sourceNow;
        if (TimeSpan.Zero < remaining && sourceRemaining < remaining)
        {
            throw new InvalidDataException("The request deadline exceeds its original remaining time.");
        }
        return new(clock, remaining, expiresAt, sourceNow);
    }
}
