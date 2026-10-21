using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>ノード実行を通らないローカル編集を、同じ離脱時方針へ接続します。</summary>
internal static class DetachedManagementPolicy
{
    /// <summary>参照、取消、開始済み回復の入口は新規編集として拒否しません。</summary>
    /// <returns>禁止した新規編集の理由、または許可です。</returns>
    public static string? GetProblem(string operation, bool detached, DetachedOperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string transferStart = "transfer.open";
        if (detached && !options.FileChange && string.Equals(operation, transferStart, StringComparison.Ordinal))
        {
            return "Detached.FileChangeDenied";
        }
        if (!detached || options.Edit)
        {
            return null;
        }
        string[] edits = ["workflows.save", "workflows.merge", "workflows.activate", "configuration.save",
            "configuration.activate", "collaboration.changes", "processes.register", "secrets.set", "internal-data.write", "internal-data.compare-exchange", "host-manager.apply", "host.power-schedules.save", "scripts.packages.change"];
        if (edits.Contains(operation, StringComparer.Ordinal))
        {
            return "Detached.EditDenied";
        }
        return null;
    }

    /// <summary>開始済み転送の完了・破棄・取消は維持し、新規転送の開始だけ用途方針で制限します。</summary>
    /// <returns>明示離脱時にも実行できるローカル要求かどうかです。</returns>
    public static bool AllowsRequest(ClusterRequest request, DetachedOperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        string openStage = "file.openStage";
        if (!options.FileChange && string.Equals(request.Kind, openStage, StringComparison.Ordinal))
        {
            return false;
        }
        string files = "file.";
        return request.Kind is "node.execute" or "node.cancel" or "safety.change" || request.Kind.StartsWith(files, StringComparison.Ordinal);
    }
}
