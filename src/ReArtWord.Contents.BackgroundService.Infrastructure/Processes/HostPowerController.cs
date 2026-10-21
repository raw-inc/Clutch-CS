using System.ComponentModel;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>永続管理受付を経た一要求だけを、確認済みローカルPCのOSへ渡します。</summary>
public sealed class HostPowerController
{
    private readonly string hostId;
    private readonly Guid instanceId;
    private readonly Func<EPeerStatus> status;
    private readonly Func<EHostPowerAction, CancellationToken, Task<NodeResult>> execute;
    private readonly Func<DateTimeOffset, bool> claimSchedule;
    private readonly TimeProvider clock;

    /// <summary>Coreの実体と状態を固定し、OSの電源操作を使用します。</summary>
    public HostPowerController(string hostId, Guid instanceId, Func<EPeerStatus> status) : this(hostId, instanceId, status, ExecuteOperatingSystemAsync)
    {
    }

    /// <summary>OSを停止せず受付境界を検証するための実行境界です。</summary>
    internal HostPowerController(string hostId, Guid instanceId, Func<EPeerStatus> status, Func<EHostPowerAction, CancellationToken, Task<NodeResult>> execute,
        Func<DateTimeOffset, bool>? claimSchedule = null, TimeProvider? timeProvider = null)
    {
        this.hostId = hostId;
        this.instanceId = instanceId;
        this.status = status;
        this.execute = execute;
        this.claimSchedule = claimSchedule ?? HostPowerScheduleLatch.Claim;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>古い表示・別PC・未確認状態を拒否してから一度だけOSへ要求します。</summary>
    /// <returns>OSの受付結果です。実際の再起動・停止完了の保証とは区別します。</returns>
    public Task<NodeResult> ExecuteAsync(HostPowerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.InstanceId != instanceId || !string.Equals(request.HostId, hostId, StringComparison.Ordinal)
            || !Enum.IsDefined(request.Action) || status() is not (EPeerStatus.Ready or EPeerStatus.Syncing))
        {
            return Task.FromResult(NodeResult.Failure("host.power.targetChanged", "The PC or its state changed. Refresh and confirm the operation again.", EOutcome.NotExecuted));
        }
        if (request.NotAfter is not null || request.ScheduledAt is not null)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (request.Action != EHostPowerAction.Shutdown || request.ScheduledAt is not { } due || request.NotAfter is not { } expiry
                || now < due || expiry <= now || expiry <= due || TimeSpan.FromMinutes(1) < expiry - due)
            {
                return Task.FromResult(NodeResult.Failure("host.power.scheduleExpired", "The scheduled shutdown window is invalid or expired.", EOutcome.NotExecuted));
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!claimSchedule(due))
                {
                    return Task.FromResult(NodeResult.Failure("host.power.scheduleAlreadyAccepted", "This scheduled shutdown was already accepted on this PC.", EOutcome.NotExecuted));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(NodeResult.Failure("host.power.scheduleClaimUnconfirmed", "The PC could not confirm the shutdown schedule claim.", EOutcome.NotExecuted));
            }
        }
        return execute(request.Action, cancellationToken);
    }

    /// <summary>シェルや利用者入力を使わず、OSの固定コマンドだけを選びます。</summary>
    /// <returns>未対応OSはnullです。強制終了オプションは付けません。</returns>
    internal static ProcessStartInfo? Command(EHostPowerAction action, string platform)
    {
        string? path = platform switch { "windows" => Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "linux" => "/usr/bin/systemctl", "macos" => "/sbin/shutdown", _ => null };
        if (path is null || !Enum.IsDefined(action))
        {
            return null;
        }
        ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        string[] arguments = platform switch
        {
            "windows" => [action switch { EHostPowerAction.Restart => "/r", _ => "/s" }, "/t", "0"],
            "linux" => [action switch { EHostPowerAction.Restart => "reboot", _ => "poweroff" }, "--no-block", "--no-ask-password"],
            _ => [action switch { EHostPowerAction.Restart => "-r", _ => "-h" }, "now"],
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    /// <summary>送信開始と受付確認不能を分け、自動で電源操作を再送しません。</summary>
    /// <returns>受付・拒否・結果不明のいずれかです。</returns>
    internal static async Task<NodeResult> ExecuteOperatingSystemAsync(EHostPowerAction action, CancellationToken cancellationToken = default)
    {
        string platform = (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()) switch
        {
            (true, _, _) => "windows",
            (_, true, _) => "linux",
            (_, _, true) => "macos",
            _ => string.Empty,
        };
        ProcessStartInfo? start = Command(action, platform);
        if (start is null || !File.Exists(start.FileName))
        {
            return NodeResult.Failure("host.power.unavailable", "The OS power command is unavailable on this PC.", EOutcome.NotExecuted);
        }
        using Process process = new() { StartInfo = start };
        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
            {
                return NodeResult.Failure("host.power.notStarted", "The OS power request could not be started.", EOutcome.NotExecuted);
            }
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return NodeResult.Success();
            }
            return NodeResult.Failure("host.power.rejected", "The OS rejected the power request. Check the Core service account permissions.", EOutcome.NotExecuted);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            EOutcome outcome = started ? EOutcome.Unknown
                                       : EOutcome.NotExecuted;
            return NodeResult.Failure("host.power.unconfirmed", "The power request result is unconfirmed. Do not resend it automatically.", outcome);
        }
    }
}
