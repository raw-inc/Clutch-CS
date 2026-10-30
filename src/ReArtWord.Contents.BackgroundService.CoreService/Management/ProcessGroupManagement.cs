using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>同じPCのアプリグループを管理APIへ接続します。</summary>
internal static class ProcessGroupManagement
{
    /// <summary>参照と永続受付を区別し、再送でグループ操作を重ねないようにします。</summary>
    internal static void Register(ManagementRegistry registry, ServiceRuntime service)
    {
        registry.Read("processes.groups.list", async (_, token) => await service.Processes.Groups.ListAsync(token).ConfigureAwait(false));
        registry.Write("processes.groups.register", async (value, token) =>
        {
            ProcessGroupRegistrationRequest request = ManagementRegistry.Body<ProcessGroupRegistrationRequest>(value);
            return new { outcome = await service.Processes.Groups.RegisterAsync(request.Definition, request.ExpectedVersion, token).ConfigureAwait(false) };
        });
        registry.Write("processes.groups.remove", async (value, token) =>
        {
            ProcessGroupRemovalRequest request = ManagementRegistry.Body<ProcessGroupRemovalRequest>(value);
            return new { outcome = await service.Processes.Groups.RemoveAsync(request.Id, request.ExpectedVersion, token).ConfigureAwait(false) };
        });
        registry.Write("processes.groups.command", async (value, token) =>
        {
            ProcessGroupCommandRequest request = ManagementRegistry.Body<ProcessGroupCommandRequest>(value);
            if (!service.ManagementStarted || service.Maintenance.IsClosed || !PeerStatusRules.CanParticipate(service.Status))
            {
                return new ProcessGroupOperationResult(EOutcome.NotExecuted, Reason: "processGroup.ownerUnavailable");
            }
            return await service.Processes.Groups.CommandAsync(request, token).ConfigureAwait(false);
        });
    }
}
