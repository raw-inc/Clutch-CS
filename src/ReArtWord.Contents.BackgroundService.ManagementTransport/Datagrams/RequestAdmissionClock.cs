using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>このCoreプロセスの起動後の経過時間を使い、要求の受付期限を検証します。</summary>
public sealed class RequestAdmissionClock(Guid instanceId, TimeProvider clock)
{
    private readonly long startedAt = clock.GetTimestamp();

    /// <summary>送信側がこのCoreの時計で受付期限を表せるよう、現在の経過時間と実体IDを返します。</summary>
    /// <returns>Coreプロセスの実体IDと、受付時計の起点からの経過時間です。</returns>
    public RequestClockSnapshot Capture() => new(instanceId, clock.GetElapsedTime(startedAt).Ticks);

    /// <summary>このプロセスの時計で発行された期限を復元します。別の起動実体の値は使用できません。</summary>
    /// <returns>要求本文の日時を変えず、単調時間で判定する受付情報です。</returns>
    public RequestAdmission Accept(RequestClockStamp timing, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(timing);
        return Create(timing.InstanceId, timing.StartByTicks, timing.SourceNow, instanceId, expiresAt);
    }

    /// <summary>再起動前のCore向けの要求や、元の残り時間を超える期限を拒否し、受信後の期限管理へ引き継ぎます。</summary>
    /// <returns>元の本文とIDを保ち、受信側での受付情報を付けた要求です。</returns>
    public ClusterRequest Accept(ClockedClusterRequest envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ClusterRequest request = envelope.Request ?? throw new InvalidDataException("The original request is missing.");
        return request with { Admission = Create(envelope.ClockInstanceId, envelope.StartByTicks, envelope.SourceNow, request.TargetInstanceId, request.ExpiresAt) };
    }

    /// <summary>ファイル転送の操作に換算済みの期限を適用します。大きなファイル断片を含むため、要求本文は再シリアライズしません。</summary>
    /// <returns>元の本文とIDを保ち、受信側での受付情報を付けた管理操作です。</returns>
    public ManagementCommand Accept(ManagementCommand command, RequestClockStamp timing)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(timing);
        return command with { Admission = Create(timing.InstanceId, timing.StartByTicks, timing.SourceNow, command.TargetInstanceId, command.ExpiresAt) };
    }

    /// <summary>Coreの実体IDと残り時間を検証し、このPCの時刻補正に影響されない期限管理を作成します。</summary>
    /// <returns>現在の単調時計を起点とする受付情報です。</returns>
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
