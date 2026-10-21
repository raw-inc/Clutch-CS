using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>最新本文と短い履歴を件数・容量・時間制限付きで保持し、永続ログへ混在させません。</summary>
public sealed class NodePreviewStore(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Workflow, string Node), Entry> entries = [];
    private readonly Dictionary<(string Workflow, string Node, Guid Run, Guid Owner, Guid Scope), NodePreviewSnapshot> progress = [];
    private readonly Dictionary<(Guid Run, Guid Owner, Guid Scope), DateTimeOffset> closedProgress = [];
    private long bytes;

    /// <summary>一つの画像・音声・映像の取得上限です。</summary>
    public static int MaximumMediaBytes => 16 * 1024 * 1024;

    /// <summary>一つのテキストの表示上限です。</summary>
    public static int MaximumTextBytes => 65536;

    /// <summary>ノードごとに保持する取得履歴の最大件数です。</summary>
    public static int MaximumHistoryEntries => 50;

    /// <summary>履歴の一行に保持するテキストの最大文字数です。</summary>
    public static int MaximumHistoryTextLength => 1024;

    /// <summary>小さい数値だけの進捗は、複数WFの並列要素も含めて有限に保持します。</summary>
    public static int MaximumProgressSessions => 1024;

    /// <summary>進捗だけを元WF実行と任意の反復単位へ分離し、媒体本文や履歴を保持しません。</summary>
    public void PublishProgress(NodeExecutionContext context, Contracts.Values.ProgressValue? value, string? problem = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            Prune(now);
            ProgressSession session = context.Progress ?? new(context.CausationId, context.PeerId, Guid.Empty, now);
            if (closedProgress.ContainsKey((session.RunId, session.OwnerInstanceId, Guid.Empty)) || closedProgress.ContainsKey((session.RunId, session.OwnerInstanceId, session.ScopeId)))
            {
                return;
            }
            (string Workflow, string Node, Guid Run, Guid Owner, Guid Scope) key = (context.WorkflowId, context.NodeId, session.RunId, session.OwnerInstanceId, session.ScopeId);
            if (MaximumProgressSessions <= progress.Count && !progress.ContainsKey(key))
            {
                progress.Remove(progress.MinBy(item => item.Value.CapturedAt).Key);
            }
            int length = value is null ? 0
                                       : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length;
            progress[key] = new(Guid.NewGuid(), context.WorkflowId, context.Revision, context.NodeId, context.PeerId, now, "text/plain", length, Problem: problem)
            { Session = session, Progress = value };
        }
    }

    /// <summary>同じ起動実体の終了範囲だけを解放し、遅れて届く旧更新を再表示しません。</summary>
    public void CompleteProgress(ProgressSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            Prune(now);
            if (closedProgress.ContainsKey((session.RunId, session.OwnerInstanceId, Guid.Empty)))
            {
                return;
            }
            foreach ((string Workflow, string Node, Guid Run, Guid Owner, Guid Scope) key in progress.Keys.Where(key => key.Run == session.RunId && key.Owner == session.OwnerInstanceId
                && (session.ScopeId == Guid.Empty || key.Scope == session.ScopeId)).ToArray())
            {
                progress.Remove(key);
            }
            if (session.ScopeId == Guid.Empty)
            {
                foreach ((Guid Run, Guid Owner, Guid Scope) key in closedProgress.Keys.Where(key => key.Run == session.RunId && key.Owner == session.OwnerInstanceId).ToArray())
                {
                    closedProgress.Remove(key);
                }
            }
            if (4096 <= closedProgress.Count)
            {
                closedProgress.Remove(closedProgress.MinBy(item => item.Value).Key);
            }
            closedProgress[(session.RunId, session.OwnerInstanceId, session.ScopeId)] = now.AddMinutes(5);
        }
    }

    /// <summary>取得した本文の所有権を分離し、同じノードの旧結果を置き換えます。</summary>
    public void Publish(NodeExecutionContext context, string mediaType, ReadOnlyMemory<byte> data, bool truncated = false, string? problem = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, MaximumMediaBytes);
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            NodePreviewSnapshot snapshot = new(Guid.NewGuid(), context.WorkflowId, context.Revision, context.NodeId, context.PeerId, now, mediaType, data.Length, truncated, problem);
            Prune(now);
            Entry? previous = entries.GetValueOrDefault((context.WorkflowId, context.NodeId));
            long number = (previous?.History[^1].Number ?? 0) + 1;
            NodePreviewHistoryEntry[] history = [.. (previous?.History ?? []).Where(item => now - item.Snapshot.CapturedAt < TimeSpan.FromMinutes(5)).TakeLast(MaximumHistoryEntries - 1), History(snapshot, number, data.Span)];
            Remove((context.WorkflowId, context.NodeId));
            while (128 <= entries.Count || 64L * 1024 * 1024 < bytes + data.Length)
            {
                Remove(entries.MinBy(pair => pair.Value.Snapshot.CapturedAt).Key);
            }
            entries[(context.WorkflowId, context.NodeId)] = new(snapshot, data.ToArray(), history);
            bytes += data.Length;
        }
    }

    /// <summary>照会間隔の間に到着した値も含め、指定ノードの履歴を新しい順で返します。</summary>
    /// <returns>最大50件の値の概要です。本文の自動再取得は行いません。</returns>
    public IReadOnlyList<NodePreviewHistoryEntry> ReadHistory(NodePreviewHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.NodeId);
        using (gate.EnterScope())
        {
            DateTimeOffset now = clock.GetUtcNow();
            Prune(now);
            return (entries.GetValueOrDefault((query.WorkflowId, query.NodeId))?.History ?? [])
                .Where(item => now - item.Snapshot.CapturedAt < TimeSpan.FromMinutes(5)).Reverse().ToArray();
        }
    }

    /// <summary>履歴の文字列量を固定し、媒体は形式とサイズだけで識別します。</summary>
    /// <returns>本文バッファを参照しない履歴です。</returns>
    private static NodePreviewHistoryEntry History(NodePreviewSnapshot snapshot, long number, ReadOnlySpan<byte> data)
    {
        string plain = "text/plain";
        if (snapshot.Problem is not null || !string.Equals(snapshot.MediaType, plain, StringComparison.Ordinal))
        {
            return new(number, snapshot, null);
        }
        int byteCount = Math.Min(data.Length, MaximumHistoryTextLength * 4);
        string text = System.Text.Encoding.UTF8.GetString(data[..byteCount]);
        bool truncated = snapshot.Truncated || byteCount < data.Length || MaximumHistoryTextLength < text.Length;
        if (MaximumHistoryTextLength < text.Length)
        {
            int length = MaximumHistoryTextLength;
            if (char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }
            text = text[..length];
        }
        return new(number, snapshot, text, truncated);
    }

    /// <summary>大きな媒体本体を含めず、表示対象の最新結果を列挙します。</summary>
    /// <returns>最大64ノードのメタデータです。</returns>
    public IReadOnlyList<NodePreviewSnapshot> Read(NodePreviewQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkflowId);
        ArgumentNullException.ThrowIfNull(query.NodeIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.NodeIds.Count, 64);
        using (gate.EnterScope())
        {
            Prune(clock.GetUtcNow());
            return query.NodeIds.Distinct(StringComparer.Ordinal).Select(id => entries.GetValueOrDefault((query.WorkflowId, id))?.Snapshot).OfType<NodePreviewSnapshot>()
                .Concat(progress.Values.Where(item => string.Equals(item.WorkflowId, query.WorkflowId, StringComparison.Ordinal) && query.NodeIds.Contains(item.NodeId, StringComparer.Ordinal)))
                .OrderBy(item => item.Session?.StartedAt ?? item.CapturedAt).ThenBy(item => item.Session?.RunId).ThenBy(item => item.Session?.ScopeId).ToArray();
        }
    }

    /// <summary>固定した取得IDの本文だけを読み、失効時に別の最新値へ切り替えません。</summary>
    /// <returns>管理通信の応答上限に収まる断片です。</returns>
    public NodePreviewChunk ReadChunk(NodePreviewChunkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Length, 32768);
        using (gate.EnterScope())
        {
            Prune(clock.GetUtcNow());
            Entry? entry = entries.Values.FirstOrDefault(item => item.Snapshot.Id == request.Id);
            if (entry is null)
            {
                NodePreviewSnapshot snapshot = progress.Values.FirstOrDefault(item => item.Id == request.Id) ?? throw new KeyNotFoundException();
                byte[] data = snapshot.Progress is null ? []
                                                        : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot.Progress);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Offset, data.Length);
                return new(request.Id, request.Offset, data.AsSpan(request.Offset, Math.Min(request.Length, data.Length - request.Offset)).ToArray());
            }
            ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Offset, entry.Data.Length);
            return new(request.Id, request.Offset, entry.Data.AsSpan(request.Offset, Math.Min(request.Length, entry.Data.Length - request.Offset)).ToArray());
        }
    }

    /// <summary>利用時に保持期限を過ぎた本文を解放します。</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach ((string Workflow, string Node, Guid Run, Guid Owner, Guid Scope) key in progress.Where(pair => TimeSpan.FromMinutes(5) <= now - pair.Value.CapturedAt).Select(pair => pair.Key).ToArray())
        {
            progress.Remove(key);
        }
        foreach ((Guid Run, Guid Owner, Guid Scope) key in closedProgress.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            closedProgress.Remove(key);
        }
        foreach ((string Workflow, string Node) key in entries.Where(pair => TimeSpan.FromMinutes(5) <= now - pair.Value.Snapshot.CapturedAt).Select(pair => pair.Key).ToArray())
        {
            Remove(key);
        }
    }

    /// <summary>件数と容量の計数を同じロック内で更新します。</summary>
    private void Remove((string Workflow, string Node) key)
    {
        if (entries.Remove(key, out Entry? previous))
        {
            bytes -= previous.Data.Length;
        }
    }

    /// <summary>参照を外部へ公開しない所有済み本文です。</summary>
    private sealed record Entry(NodePreviewSnapshot Snapshot, byte[] Data, IReadOnlyList<NodePreviewHistoryEntry> History);
}
