using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>一回の固定取得先からログを集約し、完成したZIPだけを公開します。</summary>
internal static class ArchiveExport
{
    /// <summary>各ピアで保持するログを、読取り専用の有限ページとして照会します。</summary>
    internal static void Register(PeerCluster cluster, WorkflowLogService log)
    {
        cluster.RegisterInspection("archive.logs", async (request, token) =>
        {
            LogQuery query = request.Payload.Json.Deserialize<LogQuery>(ContractJson.Options) ?? throw new InvalidDataException("Archive log query is missing.");
            if (query.Cursor is null)
            {
                await log.FlushAsync(token).ConfigureAwait(false);
            }
            return new(EOutcome.Succeeded, NodeValue.FromObject(await log.QueryAsync(query, token).ConfigureAwait(false)));
        });
    }

    /// <summary>全ピアの取得が確認できた場合だけ、転送情報を返します。</summary>
    /// <returns>完成ZIPの元操作・長さ・SHA-256です。</returns>
    internal static async Task<ArchiveOperationResult> WriteAsync(ArchiveExportRequest request, ClusterArchiveState state,
        WorkflowLogService log, PeerCluster cluster, ArchiveWorkspace workspace, CancellationToken cancellationToken = default)
    {
        PeerInfo[] members = cluster.GetPeers().OrderBy(peer => peer.PeerId, StringComparer.Ordinal).ToArray();
        if (cluster.IsDetached || !members.Any(peer => peer.InstanceId == cluster.LocalInstanceId && peer.Status == EPeerStatus.Ready))
        {
            throw new InvalidOperationException("The archive source is not Ready.");
        }
        if (request.IncludeLogs && members.Any(peer => peer.Status != EPeerStatus.Ready))
        {
            throw new InvalidOperationException("All cluster members must be available to export logs.");
        }
        if (request.IncludeLogs)
        {
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        ClusterArchiveConfiguration configuration = await state.CaptureAsync(request.IncludeLogs, cancellationToken).ConfigureAwait(false);
        Func<LogQuery, CancellationToken, Task<LogQueryResult>> read = log.QueryAsync;
        if (request.IncludeLogs)
        {
            configuration = configuration with { LogPeers = members.Select(peer => peer.PeerId).ToArray() };
            read = ClusterLogs(cluster, log, members, configuration.CapturedAt);
        }
        string temporary = workspace.FileFor(request.OperationId, "archive.tmp");
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            await ClusterArchiveFile.WriteAsync(output, configuration, read, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, workspace.FileFor(request.OperationId, "archive.zip"));
        ArchiveTransfer file = await workspace.DescribeAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        return new(request.OperationId, "completed", true, EOutcome.Succeeded, File: file);
    }

    /// <summary>通常の複数取得先検索と同じ重複除去を使用し、未確認先を隠しません。</summary>
    /// <returns>同じ検索状態を使うページ読取りです。</returns>
    private static Func<LogQuery, CancellationToken, Task<LogQueryResult>> ClusterLogs(PeerCluster cluster, WorkflowLogService log,
        IReadOnlyList<PeerInfo> members, DateTimeOffset captured)
    {
        async Task<LogQueryResult?> ReadAsync(LogSearchSource source, LogQuery query, CancellationToken token)
        {
            PeerInfo member = members.Single(peer => string.Equals(peer.PeerId, source.PeerId, StringComparison.Ordinal));
            if (member.InstanceId == cluster.LocalInstanceId)
            {
                return await log.QueryAsync(query, token).ConfigureAwait(false);
            }
            ClusterResponse reply = await cluster.InspectAsync(new(Guid.NewGuid(), "archive.logs", member.PeerId, member.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(15), NodeValue.FromObject(query)), token).ConfigureAwait(false);
            if (reply.Outcome != EOutcome.Succeeded)
            {
                return null;
            }
            return reply.Value?.Json.Deserialize<LogQueryResult>(ContractJson.Options);
        }
        LogSearchSession search = new(new(Until: captured, Limit: 1000, MaximumBytes: 524288),
            members.Select(peer => new LogSearchSource(cluster.LocalClusterId, peer.PeerId)), ReadAsync, maximumResultBytes: 524288);
        int index = 0;
        return async (_, token) =>
        {
            LogSearchPage page = await search.NextAsync(token).ConfigureAwait(false);
            string? cursor = null;
            if (page.HasMore)
            {
                cursor = (++index).ToString(CultureInfo.InvariantCulture);
            }
            return new(page.Entries.Select(row => row.Entry).ToArray(), cursor, page.Complete, page.UnconfirmedSources,
                page.Entries.Select(row => row.Omission).OfType<LogEntryOmission>().ToArray());
        };
    }
}
