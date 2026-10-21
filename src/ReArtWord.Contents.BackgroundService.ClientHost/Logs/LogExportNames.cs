using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>一出力内の名前照会を集約し、保持するWF数とノード名を制限します。</summary>
internal sealed class LogExportNames(Func<LogSearchSource, LogNamesQuery, CancellationToken, Task<LogNames>> read)
{
    private readonly Dictionary<(LogSearchSource Source, string Workflow), Cached> cache = [];

    /// <summary>次のページに必要な名前だけを取得します。定義削除・取得失敗時はIDへ戻します。</summary>
    /// <returns>名前を確認したページの準備です。</returns>
    internal async Task PrepareAsync(IReadOnlyList<ScopedLogEntry> entries, CancellationToken cancellationToken = default)
    {
        IGrouping<(LogSearchSource Source, string Workflow), ScopedLogEntry>[] groups = entries.Where(row => row.Entry.WorkflowId is not null)
            .GroupBy(row => (row.Source, row.Entry.WorkflowId!)).ToArray();
        HashSet<(LogSearchSource Source, string Workflow)> required = groups.Select(group => group.Key).ToHashSet();
        foreach (IGrouping<(LogSearchSource Source, string Workflow), ScopedLogEntry> group in groups)
        {
            cache.TryGetValue(group.Key, out Cached? previous);
            string[] nodes = group.Select(row => row.Entry.NodeId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (previous is not null && (previous.Failed || nodes.All(previous.Queried.Contains)))
            {
                continue;
            }
            if (64 <= cache.Count && previous is null)
            {
                cache.Remove(cache.Keys.First(key => !required.Contains(key)));
            }
            Cached current = previous ?? new();
            if (1024 < current.Queried.Count + nodes.Length)
            {
                current = new();
            }
            cache[group.Key] = current;
            try
            {
                string[][] chunks = nodes.Chunk(256).ToArray();
                if (chunks.Length == 0)
                {
                    chunks = [[]];
                }
                foreach (string[] chunk in chunks)
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    LogNames names = await read(group.Key.Source, new(group.Key.Workflow, chunk), timeout.Token).ConfigureAwait(false);
                    if (!string.Equals(current.Revision, names.Revision, StringComparison.Ordinal))
                    {
                        current.Queried.Clear();
                        current.Nodes.Clear();
                    }
                    current.Name = names.WorkflowName;
                    current.Revision = names.Revision;
                    foreach (string id in chunk)
                    {
                        current.Queried.Add(id);
                        current.Nodes[id] = names.Nodes.GetValueOrDefault(id) ?? id;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current.Failed = true;
            }
        }
    }

    /// <summary>保存名のない対象は元IDを残し、別WFの同名ノードと混ぜません。</summary>
    /// <returns>表示名と、その名前を確認した保存版です。</returns>
    internal (string? Workflow, string? Node, string? Revision, bool Resolved) Get(ScopedLogEntry row)
    {
        if (row.Entry.WorkflowId is not { } workflow)
        {
            return (null, row.Entry.NodeId, null, true);
        }
        cache.TryGetValue((row.Source, workflow), out Cached? value);
        string? node = row.Entry.NodeId;
        if (node is not null)
        {
            node = value?.Nodes.GetValueOrDefault(node) ?? node;
        }
        return (value?.Name ?? workflow, node, value?.Revision, value is { Failed: false, Name: not null });
    }

    /// <summary>最大一WF分の表示情報だけを保持します。</summary>
    private sealed class Cached
    {
        /// <summary>名前本文を含めず、保持件数だけを診断表示します。</summary>
        /// <returns>キャッシュの大きさです。</returns>
        public override string ToString() => "Log names: " + Nodes.Count;

        internal string? Name
        {
            get; set;
        }

        internal string? Revision
        {
            get; set;
        }

        internal bool Failed
        {
            get; set;
        }

        internal HashSet<string> Queried
        {
            get;
        } = new(StringComparer.Ordinal);

        internal Dictionary<string, string> Nodes
        {
            get;
        } = new(StringComparer.Ordinal);

    }
}
