using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>編集下書きと編集者情報を保存操作から分離して公開します。</summary>
internal static class CollaborationManagement
{
    /// <summary>対象ワークフローを各入力契約で明示します。</summary>
    public static void Register(ManagementRegistry registry, CollaborationService service, DraftAutoApply apply)
    {
        registry.Read("collaboration.apply-result", async (value, cancellation) => await apply.ReadResultAsync(ManagementRegistry.Id(value), cancellation).ConfigureAwait(false));
        registry.Read("collaboration.draft", async (value, cancellation) => await service.GetDraftAsync(ManagementRegistry.Id(value), cancellation).ConfigureAwait(false) ?? throw new KeyNotFoundException());
        registry.Transient("collaboration.changes", async (value, cancellation) => await service.ApplyChangesAsync(ManagementRegistry.Body<DraftChangeRequest>(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("collaboration.presence", value => service.GetPresence(ManagementRegistry.Id(value)));
        registry.Transient("collaboration.update-presence", async (value, cancellation) =>
        {
            await service.UpdatePresenceAsync(ManagementRegistry.Body<PresenceUpdate>(value), cancellation).ConfigureAwait(false);
            return null;
        });
    }
}
