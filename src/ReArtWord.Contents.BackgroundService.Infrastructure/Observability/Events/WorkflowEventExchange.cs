using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;

/// <summary>共通事象を現在接続中のPeerへ配信し、受信事象を送り返しません。</summary>
public sealed class WorkflowEventExchange
{
    private static string Topic => "workflow.events";
    private static string BatchTopic => "workflow.events.batch";
    private readonly PeerCluster cluster;
    private readonly WorkflowEventHub events;
    private readonly Channel<(WorkflowEvent Item, int Size)> outgoing = Channel.CreateBounded<(WorkflowEvent, int)>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <summary>既存の一時通知経路と、ローカルの購読先へ接続します。</summary>
    public WorkflowEventExchange(PeerCluster cluster, WorkflowEventHub events)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(events);
        this.cluster = cluster;
        this.events = events;
        cluster.RegisterTransientHandler(Topic, ReceiveAsync);
        cluster.RegisterTransientHandler(BatchTopic, ReceiveBatchAsync);
    }

    /// <summary>通常処理から独立して転送し、Host終了時に購読を解除します。</summary>
    /// <returns>転送ループの終了。</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        events.LocalPublished += Enqueue;
        try
        {
            while (await outgoing.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                List<(WorkflowEvent Item, int Size)> batch = [];
                while (batch.Count < 32 && outgoing.Reader.TryRead(out (WorkflowEvent Item, int Size) item))
                {
                    batch.Add(item);
                }
                bool compatible = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).All(peer => peer.BufferedRuntime);
                int offset = 0;
                while (offset < batch.Count)
                {
                    int count = 1, bytes = batch[offset].Size;
                    while (compatible && offset + count < batch.Count && bytes + batch[offset + count].Size < 30720)
                    {
                        bytes += batch[offset + count].Size;
                        count++;
                    }
                    WorkflowEvent[] items = batch.Skip(offset).Take(count).Select(entry => entry.Item).ToArray();
                    NodeValue grouped = NodeValue.FromObject(items);
                    if (1 < count && grouped.PayloadSize() <= 32768)
                    {
                        await PublishAsync(BatchTopic, grouped, count, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (WorkflowEvent item in items)
                        {
                            await PublishAsync(Topic, NodeValue.FromObject(item), 1, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    offset += count;
                }
            }
        }
        finally
        {
            events.LocalPublished -= Enqueue;
        }
    }

    /// <summary>到着済みの小さい事象だけをまとめ、意図した待機や再送は加えません。</summary>
    /// <returns>今回の一度だけの配送試行です。</returns>
    private async Task PublishAsync(string topic, NodeValue value, int count, CancellationToken cancellationToken = default)
    {
        try
        {
            await cluster.PublishRemoteTransientAsync(topic, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            for (int index = 0; index < count; index++)
            {
                events.RecordDrop("event.transportUnavailable");
            }
        }
    }

    /// <summary>転送待ちが満杯なら欠落を記録し、発行元の処理は継続します。</summary>
    private void Enqueue(WorkflowEvent item, int bytes)
    {
        if (!outgoing.Writer.TryWrite((item, bytes)))
        {
            events.RecordDrop("event.outgoingFull");
        }
    }

    /// <summary>通知元と発行元が同じPeer実体であることを検証します。</summary>
    /// <returns>今回の取り込み処理の完了。</returns>
    private Task ReceiveAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            WorkflowEvent? item = value.Json.Deserialize<WorkflowEvent>(ContractJson.Options);
            ReceiveItem(peerId, instance, item);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            events.RecordDrop("event.invalid");
        }
        return Task.CompletedTask;
    }

    /// <summary>受信件数を先に制限し、各事象にも従来と同じ発生元確認を適用します。</summary>
    /// <returns>一組の取り込みの完了です。</returns>
    private Task ReceiveBatchAsync(string peerId, Guid instance, NodeValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (value.Kind != EValueKind.Json || value.Json.ValueKind != JsonValueKind.Array || value.Json.GetArrayLength() is < 1 or > 32)
            {
                events.RecordDrop("event.invalid");
                return Task.CompletedTask;
            }
            foreach (WorkflowEvent? item in value.Json.Deserialize<WorkflowEvent[]>(ContractJson.Options)!)
            {
                ReceiveItem(peerId, instance, item);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            events.RecordDrop("event.invalid");
        }
        return Task.CompletedTask;
    }

    /// <summary>個別通知でも一括通知でも、同じ発生元と現在実体を確認します。</summary>
    private void ReceiveItem(string peerId, Guid instance, WorkflowEvent? item)
    {
        if (item is null || item.PeerInstanceId != instance || !string.Equals(item.PeerId, peerId, StringComparison.Ordinal)
            || !cluster.GetPeers().Any(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal) && peer.InstanceId == instance))
        {
            events.RecordDrop("event.originMismatch");
            return;
        }
        events.Receive(item);
    }
}
