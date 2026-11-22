using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>ブラウザーの時計を受付判定に使わず、このClientHostの経過時間へ期限を固定します。</summary>
internal sealed class ClientRequestClock
{
    private readonly TimeProvider clock;
    private readonly RequestAdmissionClock admission;

    /// <summary>ClientHostの再起動ごとに別の時計として識別します。</summary>
    public ClientRequestClock(TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        admission = new(Guid.NewGuid(), clock);
    }

    /// <summary>時計確認の応答を作成します。この照会だけで操作は開始しません。</summary>
    /// <returns>日付表記と、時刻補正に依存しない経過時間です。</returns>
    internal ClientRequestClockSnapshot Capture()
    {
        RequestClockSnapshot sample = admission.Capture();
        return new(sample.InstanceId, sample.ElapsedTicks, clock.GetUtcNow());
    }

    /// <summary>初回要求の期限を検証します。保存済み操作はこの時計へ再換算せず、元の台帳を使用します。</summary>
    /// <returns>通信中の時間も差し引いた受付情報です。</returns>
    internal RequestAdmission Accept(ClockedNodeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);
        ArgumentNullException.ThrowIfNull(command.Timing);
        TimeSpan lifetime = command.Request.ExpiresAt - command.Timing.SourceNow;
        if (lifetime <= TimeSpan.Zero || TimeSpan.FromMinutes(5) < lifetime || command.Request.Id == Guid.Empty)
        {
            throw new InvalidDataException("操作の受付期限が不正です。");
        }
        return admission.Accept(command.Timing, command.Request.ExpiresAt);
    }
}
