using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Conflicts;

/// <summary>永続競合を編集草稿の競合と区別し、固定候補の比較と元操作照会へ接続します。</summary>
internal static class ConflictManagement
{
    /// <summary>一覧はメタデータだけ、候補値は明示した一件だけを返します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime)
    {
        registry.Read("conflicts.list", async (value, cancellation) =>
        {
            ConflictListQuery query = new();
            if (value is not null)
            {
                query = ManagementRegistry.Body<ConflictListQuery>(value);
            }
            return await ConflictListing.ReadAsync(runtime, query, cancellation).ConfigureAwait(false);
        });
        registry.Read("conflicts.detail", async (value, cancellation) => await ConflictListing.DetailAsync(runtime,
            ManagementRegistry.Body<ConflictDetailRequest>(value), cancellation).ConfigureAwait(false));
        registry.Write("conflicts.resolve", async (value, cancellation) => await runtime.Conflicts.ResolveAsync(
            ManagementRegistry.Body<ConflictResolveRequest>(value), cancellation).ConfigureAwait(false));
        registry.Read("conflicts.operations", async (value, cancellation) => await runtime.Conflicts.GetOperationAsync(
            ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
    }
}
