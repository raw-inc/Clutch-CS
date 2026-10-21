using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Power;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>明示されたPC共通設定だけを監視し、同一無操作期間の要求を再送しません。</summary>
[SupportedOSPlatform("windows")]
public sealed class IdleShutdownWorker(ILogger<IdleShutdownWorker> logger, Func<CancellationToken, Task<DeviceUserActivity?>> readActivity) : Microsoft.Extensions.Hosting.BackgroundService
{
    private static readonly Action<ILogger, Exception?> reportFailure = LoggerMessage.Define(LogLevel.Warning, new EventId(7801, "IdleShutdownUnconfirmed"), "Idle shutdown could not be verified; the same shutdown request will not be retried.");
    private static readonly Action<ILogger, Contracts.Execution.EOutcome, Exception?> reportOutcome = LoggerMessage.Define<Contracts.Execution.EOutcome>(LogLevel.Information, new EventId(7802, "IdleShutdownRequested"), "Idle shutdown request completed with {Outcome}; the same idle episode will not be retried.");

    /// <summary>停止中は既定で何もせず、設定時だけ確認します。</summary>
    /// <returns>サービス終了までの監視です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        bool reported = false;
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
                reported = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or Win32Exception
                || exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                if (!reported)
                {
                    reportFailure(logger, exception);
                    reported = true;
                }
            }
        }
    }

    /// <summary>無効時は書込みもセッション照会も行いません。</summary>
    /// <returns>OSへの一回限りの受付までです。</returns>
    private async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        DeviceIdleShutdownPolicy policy = WindowsIdleShutdownStore.Read();
        if (policy.AcSeconds == 0 && policy.DcSeconds == 0)
        {
            return;
        }
        using FileStream? lease = DeviceHostLease.TryAcquire(DeviceHostStorage.DirectoryPath);
        if (lease is null)
        {
            return;
        }
        IdleShutdownController controller = new(WindowsIdleShutdownStore.Read, WindowsIdleInput.OnAcPower, readActivity,
            WindowsIdleShutdownStore.Claim, token => HostPowerController.ExecuteOperatingSystemAsync(EHostPowerAction.Shutdown, token), TimeProvider.System);
        Contracts.Nodes.NodeResult? result = await controller.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            reportOutcome(logger, result.Outcome, null);
        }
    }
}
