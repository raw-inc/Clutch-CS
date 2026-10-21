using System.IO.Enumeration;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>OS通知の一件境界とroot待受の利用可能性を分けます。</summary>
internal sealed class FileWatchNode : ISourceNode
{
    private readonly FileAccessPolicy policy;

    /// <summary>共通許可範囲でwatchを作成します。</summary>
    internal FileWatchNode(FileAccessPolicy policy)
    {
        this.policy = policy;
    }

    /// <summary>継続監視と一件filter設定です。</summary>
    public NodeDescriptor Descriptor => new("file.watch", "File watcher", "File system", "許可root内のOS変更通知を一件ずつ出力します。", ENodeKind.Source, EExecutionLocation.ExplicitPeer, [], [new("value")], ["success"], NodeSettings.Schema("""
        {"type":"object","required":["path"],"properties":{"path":{"type":"string","minLength":1},"recursive":{"type":"boolean","default":false},"filter":{"type":"string","default":"*"},"count":{"type":"integer","minimum":0,"maximum":2147483647,"default":0}}}
        """));

    /// <summary>監視設定の構造を検証します。実体は実行Peerで確認します。</summary>
    /// <returns>訂正する問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>root喪失後も同じ許可パスの復帰を待ちます。</summary>
    /// <returns>予定停止または明示取消の終端です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        NodeSettings settings = new(context.Initial.Settings);
        string path = settings.ReadText("path") ?? string.Empty;
        int received = 0;
        while (!context.IsStopRequested && !lifetime.IsCancellationRequested)
        {
            try
            {
                policy.ValidatePath(path, true);
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException();
                }
                bool ended = await WatchAsync(context, path, settings.ReadBoolean("recursive"), () => ++received, cancellationToken).ConfigureAwait(false);
                if (ended)
                {
                    return Terminal(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await context.ReportStatusAsync(new(ESourceState.Unavailable, "file.watch.unavailable", "同じ許可rootの復旧を待機します。"), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Terminal(cancellationToken);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Terminal(cancellationToken);
            }
        }
        return Terminal(cancellationToken);
    }

    /// <summary>一つのOS watcher寿命を所有し、過負荷と欠落を明示します。</summary>
    /// <returns>件数または予定停止で正常終了したかです。</returns>
    private async Task<bool> WatchAsync(NodeSourceContext context, string path, bool recursive, Func<int> incrementReceived, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        Channel<NodeEmission> queue = Channel.CreateBounded<NodeEmission>(1024);
        int overflow = 0;
        using FileSystemWatcher watcher = new(path)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += Record;
        watcher.Changed += Record;
        watcher.Deleted += Record;
        watcher.Renamed += Record;
        watcher.Error += (_, _) => Interlocked.Exchange(ref overflow, 1);
        watcher.EnableRaisingEvents = true;
        await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
        try
        {
            while (!lifetime.IsCancellationRequested && !context.IsStopRequested)
            {
                policy.ValidatePath(path, true);
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException();
                }
                if (Interlocked.Exchange(ref overflow, 0) != 0)
                {
                    NodeExecutionContext failedItem = context.BeginItem();
                    await context.EmitAsync(failedItem, NodeResult.Failure("file.watch.events.lost", "OS通知または内部待ち行列の欠落が発生しました。"), cancellationToken).ConfigureAwait(false);
                }
                using CancellationTokenSource poll = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                poll.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    NodeEmission item = await queue.Reader.ReadAsync(poll.Token).ConfigureAwait(false);
                    await context.EmitAsync(item.Context, item.Result, cancellationToken).ConfigureAwait(false);
                    int count = new NodeSettings(item.Context.Settings).ReadInt32("count");
                    int total = incrementReceived();
                    if (0 < count && count <= total)
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    // 空待機だけを起こしてrootの存続を確認します。
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 通常停止では取得済み通知をこの後drainします。
        }
        watcher.EnableRaisingEvents = false;
        while (queue.Reader.TryRead(out NodeEmission? item))
        {
            await context.EmitAsync(item.Context, item.Result, cancellationToken).ConfigureAwait(false);
        }
        return true;

        // OSが通知した入口で一件の設定を固定します。
        void Record(object sender, FileSystemEventArgs args)
        {
            if (context.IsStopRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            NodeExecutionContext item = context.BeginItem();
            string pattern = new NodeSettings(item.Settings).ReadText("filter", "*") ?? string.Empty;
            if (!FileSystemName.MatchesSimpleExpression(pattern, args.Name, OperatingSystem.IsWindows()))
            {
                return;
            }
            NodeResult result;
            try
            {
                policy.ValidatePath(args.FullPath, true);
                string? oldPath = null;
                if (args is RenamedEventArgs renamed)
                {
                    oldPath = renamed.OldFullPath;
                }
                result = NodeResult.Success(NodeValue.FromObject(new { path = args.FullPath, oldPath, change = args.ChangeType.ToString() }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = NodeResult.Failure("file.watch.item.invalid", "通知対象が許可範囲またはlink制約を満たしません。");
            }
            if (!queue.Writer.TryWrite(new(item, result)))
            {
                Interlocked.Exchange(ref overflow, 1);
            }
        }
    }

    /// <summary>予定停止と緊急取消を分けます。</summary>
    /// <returns>待受の終端です。</returns>
    private static NodeResult Terminal(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("file.watch.cancelled", "ファイル監視が取り消されました。", Contracts.Execution.EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }
}
