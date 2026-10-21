using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Tracing;

/// <summary>秘密値を生バイト全体から除いた後に通信内容の記録範囲を制限します。</summary>
public sealed class TraceRawLogger : INodeTrace
{
    private readonly PeerCluster cluster;
    private readonly IWorkflowLog log;
    private readonly LogRedactor redactor;

    /// <summary>現在の参加状態と既存ログ保護境界へ接続します。</summary>
    public TraceRawLogger(PeerCluster cluster, IWorkflowLog log, SecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(secrets);
        this.cluster = cluster;
        this.log = log;
        redactor = new LogRedactor(secrets);
    }

    /// <summary>認証headersを受け取らずbody/datagram/frameだけを保護して記録します。</summary>
    /// <returns>記録受付または安全な省略の完了です。</returns>
    public async ValueTask WriteRawAsync(NodeExecutionContext context, string protocol, bool outgoing, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        TracePolicy? policy = context.TracePolicy;
        if (policy?.Enabled != true || policy.Selection?.RawCommunication == false)
        {
            return;
        }
        PeerInfo[] connected = cluster.GetPeers().Where(peer => peer.Status != EPeerStatus.Detached).ToArray();
        if (!connected.Any(peer => peer.InstanceId == cluster.LocalInstanceId) || connected.Any(peer => !PeerStatusRules.CanParticipate(peer.Status)))
        {
            return;
        }
        try
        {
            IReadOnlyList<string> patterns = await redactor.ReadPatternsAsync(cancellationToken).ConfigureAwait(false);
            WorkflowLogEntry entry = new(Guid.NewGuid(), context.TimeProvider.GetUtcNow(), "DEBUG", context.PeerId,
                "通信内容の指定範囲を記録しました。", context.WorkflowId, context.NodeId, context.CausationId, NodeValue.FromBytes(payload), Hooks: context.Hooks);
            WorkflowLogEntry protectedEntry = LogRedactor.Redact(entry, patterns);
            ReadOnlyMemory<byte> protectedBytes = protectedEntry.Data!.Binary;
            bool truncated = false;
            if (policy.Selection?.RawByteLimit is int limit)
            {
                if (limit <= 0)
                {
                    return;
                }
                truncated = limit < protectedBytes.Length;
                protectedBytes = protectedBytes[..Math.Min(limit, protectedBytes.Length)];
            }
            await log.WriteAsync(protectedEntry with { Data = NodeValue.FromBytes(protectedBytes), Raw = new TraceRawInfo(protocol, outgoing, payload.Length, truncated) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 秘密の取得不能・記録不能で生値を代替出力せず、本来の通信を継続します。
        }
    }
}
