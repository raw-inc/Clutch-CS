using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Management;

namespace ReArtWord.Contents.BackgroundService.CoreService.Updates;

/// <summary>Updaterの管理操作を共通UDP管理registryへ登録します。</summary>
internal static class UpdateManagement
{
    /// <summary>HTTP待受なしで既存ドメイン操作を公開します。</summary>
    /// <returns>永続停止受付を周期確認する接続実装です。</returns>
    public static LocalUpdateAdapter Register(ManagementRegistry registry, ServiceRuntime runtime, UpdatesOptions options, IHostApplicationLifetime lifetime)
    {
        LocalUpdateAdapter local = new(runtime, options, lifetime);
        UpdateFileReceiver files = new(() => local.Agent, options);
        ClusterUpdateLauncher launcher = local.Launcher;
        Runtime.Observability.FleetUpdateObservationStore progress = new();
        registry.Transient("updates.fleet.observe", (body, _) => Task.FromResult<object?>(progress.Observe(ManagementRegistry.Body<Contracts.Updates.Fleet.FleetUpdateObservation>(body))));
        registry.ReadValue("updates.fleet.observed", _ => progress.Read());
        registry.ReadValue("updates.schedules.status", _ => new { local.Scheduler.Problem });
        registry.Read("updates.schedules.list", async (_, token) => await local.Schedules.ListAsync(token).ConfigureAwait(false));
        registry.Read("updates.schedules.read", async (body, token) => await local.Schedules.ReadAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Read("updates.schedules.operation", async (body, token) => await local.Schedules.OperationAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.schedules.save", async (body, token) => await local.Schedules.SaveAsync(ManagementRegistry.Body<UpdateScheduleRequest>(body), token).ConfigureAwait(false));
        registry.Write("updates.schedules.cancel", async (body, token) => await local.Schedules.CancelAsync(ManagementRegistry.Body<UpdateScheduleCancelRequest>(body), token).ConfigureAwait(false));
        registry.ReadValue("updates.events", _ => new { local.Events.Pending, local.Events.Problem });
        registry.Write("updates.events.enqueue", async (body, token) => new { outcome = await local.Events.EnqueueAsync(ManagementRegistry.Body<UpdateNotification>(body), token).ConfigureAwait(false) });
        registry.ReadValue("updates.local.platform", _ => RuntimeInformation.RuntimeIdentifier);
        registry.ReadValue("updates.local.stream.capabilities", _ => local.Stream.Capabilities);
        registry.Write("updates.local.stream.open", async (body, token) => await local.Stream.OpenAsync(ManagementRegistry.Body<UpdateStreamRequest>(body), token).ConfigureAwait(false));
        registry.Write("updates.local.stream.observer", async (body, token) => await local.Stream.OpenAsync(ManagementRegistry.OperationId(body),
            runtime.Cluster.ManagementSourceAddress ?? throw new InvalidOperationException("管理要求の送信元を確認できません。"), token).ConfigureAwait(false));
        registry.ReadValue("updates.local.stream.read", body => local.Stream.Read(ManagementRegistry.OperationId(body)));
        registry.Write("updates.start", async (body, token) => await launcher.StartAsync(ManagementRegistry.Body<ClusterUpdateRequest>(body), token).ConfigureAwait(false));
        registry.Read("updates.read", async (body, token) => await launcher.ReadAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.prepare", async (body, token) => await local.PrepareAsync(ManagementRegistry.Body<ClusterUpdateRequest>(body), token).ConfigureAwait(false));
        registry.Write("updates.local.files.open", async (body, token) => await files.OpenAsync(ManagementRegistry.Body<UpdateFileRequest>(body), token).ConfigureAwait(false));
        registry.Write("updates.local.files.write", async (body, token) => await files.WriteAsync(ManagementRegistry.Body<FileChunkWriteRequest>(body), token).ConfigureAwait(false));
        registry.Write("updates.local.files.commit", async (body, token) => await files.CommitAsync(ManagementRegistry.Body<FileStageToken>(body), token).ConfigureAwait(false));
        registry.Write("updates.local.seal", async (body, token) => await local.Agent.SealAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.dispatch", async (body, token) => await local.DispatchAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Read("updates.local.read", async (body, token) => await local.Agent.ReadAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.rollback", async (body, token) => await local.RollbackAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.release", async (body, token) => await local.ReleaseAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.hold", async (body, token) => await local.HoldAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Write("updates.local.exclude", async (_, token) => await local.ExcludeAsync(token).ConfigureAwait(false));
        registry.Write("updates.local.stop", async (body, token) => await local.AcceptStopAsync(ManagementRegistry.Body<LocalUpdateStopRequest>(body), token).ConfigureAwait(false));
        return local;
    }
}
