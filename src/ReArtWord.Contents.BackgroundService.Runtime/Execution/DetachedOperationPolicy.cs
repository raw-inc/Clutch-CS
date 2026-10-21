using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Execution;

/// <summary>離脱時の用途制限を既存分類と実ファイル範囲から一箇所で判定します。</summary>
public static class DetachedOperationPolicy
{
    private static readonly HashSet<string> computeCategories = new(["Data", "Control", "State", "Scheduling", "Resources"], StringComparer.Ordinal);
    private static readonly HashSet<string> processReads = new(["process.observe", "process.waitReady"], StringComparer.Ordinal);

    /// <summary>継続待受の開始と次入力へ同じ依存・離脱判定を適用します。</summary>
    /// <returns>拒否理由またはnullです。</returns>
    internal static string? GetSourceProblem(IPeerDirectory peers, Func<string, NodeDefinition, string?>? dependencyCheck, DetachedOperationOptions options, string workflowId, NodeDefinition node, NodeDescriptor descriptor)
    {
        string? problem = dependencyCheck?.Invoke(workflowId, node);
        if (problem is null && peers.GetPeers().Any(peer => peer.InstanceId == peers.LocalInstanceId && peer.Status == EPeerStatus.Detached))
        {
            problem = GetProblem(descriptor, node.Settings, options);
        }
        return problem;
    }

    /// <summary>待受開始・一件境界で判定可能な用途を確認します。</summary>
    /// <param name="descriptor">既存ノード情報。</param>
    /// <param name="settings">一件の設定。</param>
    /// <param name="options">離脱時の明示許可。</param>
    /// <returns>拒否理由、または許可時nullです。</returns>
    public static string? GetProblem(NodeDescriptor descriptor, JsonElement settings, DetachedOperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        if (Unrestricted(options))
        {
            return null;
        }
        string communication = "Communication";
        string ai = "AI";
        string media = "Media";
        string files = "File system";
        string processes = "Processes";
        if (computeCategories.Contains(descriptor.Category))
        {
            return Require(options.Compute, "離脱中の計算・観測が許可されていません。");
        }
        if (string.Equals(descriptor.Category, communication, StringComparison.Ordinal))
        {
            return Require(options.ExternalCommunication, "離脱中の外部通信が許可されていません。");
        }
        if (string.Equals(descriptor.Category, ai, StringComparison.Ordinal))
        {
            string video = "video";
            string property = "mediaKind";
            string? kind = null;
            if (settings.TryGetProperty(property, out JsonElement value))
            {
                kind = value.GetString();
            }
            return Require(options.ExternalCommunication, "離脱中の外部通信が許可されていません。")
                ?? Require(!string.Equals(kind, video, StringComparison.Ordinal) || options.ProcessChange, "動画解析に必要なprocess操作が離脱中は許可されていません。");
        }
        if (string.Equals(descriptor.Category, media, StringComparison.Ordinal))
        {
            return Require(options.ProcessChange, "変換に必要なprocess操作が離脱中は許可されていません。")
                ?? Require(options.FileChange, "変換出力のファイル変更が離脱中は許可されていません。");
        }
        if (string.Equals(descriptor.Category, processes, StringComparison.Ordinal))
        {
            string watch = "process.watch";
            bool read = processReads.Contains(descriptor.Type) || string.Equals(descriptor.Type, watch, StringComparison.Ordinal)
                && settings.TryGetProperty("autoRecover", out JsonElement recovery) && recovery.ValueKind == JsonValueKind.False;
            if (read)
            {
                return Require(options.Compute, "離脱中のprocess観測が許可されていません。");
            }
            return Require(options.ProcessChange, "離脱中のprocess操作・自動復旧が許可されていません。");
        }
        if (string.Equals(descriptor.Category, files, StringComparison.Ordinal))
        {
            if (descriptor.Kind == ENodeKind.Source)
            {
                return Require(options.Compute, "離脱中のファイル監視が許可されていません。");
            }
            return null;
        }
        return "離脱時制限に対するノードの用途を判定できません。";
    }

    /// <summary>副作用開始前に実footprintでファイル読取りと変更を区別します。</summary>
    /// <param name="node">実行ノード。</param>
    /// <param name="context">固定した一件。</param>
    /// <param name="options">離脱時の明示許可。</param>
    /// <param name="cancellationToken">取消通知。</param>
    /// <returns>拒否理由、または許可時nullです。</returns>
    public static async ValueTask<string?> GetProblemAsync(INode node, NodeExecutionContext context, DetachedOperationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        string? problem = GetProblem(node.Descriptor, context.Settings, options);
        if (problem is not null || Unrestricted(options))
        {
            return problem;
        }
        string files = "File system";
        if (node is IFileFootprintNode file)
        {
            IReadOnlyList<FilePathAccess> paths = await file.GetFileAccessAsync(context, cancellationToken).ConfigureAwait(false);
            if (paths.Any(static path => path.Write))
            {
                return Require(options.FileChange, "離脱中のファイル変更が許可されていません。");
            }
            if (string.Equals(node.Descriptor.Category, files, StringComparison.Ordinal))
            {
                return Require(options.Compute, "離脱中のファイル読取りが許可されていません。");
            }
        }
        if (string.Equals(node.Descriptor.Category, files, StringComparison.Ordinal) && node.Descriptor.Kind == ENodeKind.Action)
        {
            return "実ファイルの読書き範囲を確認できません。";
        }
        return null;
    }

    /// <summary>ノードの用途分類が不要な全操作許可かを確認します。</summary>
    /// <returns>全ノード操作が許可されているか。</returns>
    private static bool Unrestricted(DetachedOperationOptions options) => options.Compute && options.FileChange && options.ProcessChange && options.ExternalCommunication;

    /// <summary>許可時は拒否理由を生成しません。</summary>
    /// <returns>拒否理由またはnullです。</returns>
    private static string? Require(bool allowed, string reason)
    {
        if (allowed)
        {
            return null;
        }
        return reason;
    }
}
