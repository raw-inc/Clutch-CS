using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;

/// <summary>本文配送前だけUDPへ戻し、配送後の欠測を副作用の再送へ変えません。</summary>
internal sealed class PeerStreamClient(PeerCluster cluster, Func<LocalNetworkBinding> binding,
    Func<WireMessage, CancellationToken, Task<WireMessage?>> datagram, TimeSpan timeout, int maximum, bool enabled = true, CancellationToken shutdown = default) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<PeerStreamConnection>> connections = new(StringComparer.Ordinal);
    private int idleConnections;
    private readonly SemaphoreSlim requests = new(8, 8);
    private readonly SemaphoreSlim controls = new(16, 16);
    private readonly SemaphoreSlim confirmations = new(8, 8);
    private readonly TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int active;
    private bool closed;

    /// <summary>配送開始前だけ従来経路へ切り替え、同じ封筒と要求IDを維持します。</summary>
    /// <returns>選択した経路で確認できた応答です。</returns>
    // 抑制理由: capacityはこの実体の共有Semaphoreの借用です。全送信の終了後にDisposeAsyncで破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "共有Semaphoreを借用し、送信ごとには破棄しません。所有者のDisposeAsyncで終了後に破棄します。")]
    internal async Task<WireMessage?> ExchangeAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        using (gate.EnterScope())
        {
            ObjectDisposedException.ThrowIf(closed, this);
            active++;
        }
        bool entered = false;
        SemaphoreSlim capacity = PeerStreamFrame.Lane(message.Kind) switch
        {
            0 => requests,
            2 => confirmations,
            _ => controls,
        };
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
            await capacity.WaitAsync(lifetime.Token).ConfigureAwait(false);
            entered = true;
            (bool sent, WireMessage? reply) = await TryExchangeAsync(message, lifetime.Token).ConfigureAwait(false);
            if (!sent)
            {
                reply = await datagram(message, lifetime.Token).ConfigureAwait(false);
            }
            if (reply is not null && reply.ReplyTo == message.Id && reply.InstanceId == message.TargetInstanceId && reply.TargetInstanceId == message.InstanceId
                && string.Equals(reply.SenderId, message.TargetId, StringComparison.Ordinal) && string.Equals(reply.ClusterId, message.ClusterId, StringComparison.Ordinal))
            {
                cluster.ObserveTraffic(reply.SenderId, reply.InstanceId);
            }
            return reply;
        }
        finally
        {
            if (entered)
            {
                capacity.Release();
            }
            using (gate.EnterScope())
            {
                active--;
                if (closed && active == 0)
                {
                    idle.TrySetResult();
                }
            }
        }
    }

    /// <summary>固定した相手への対応確認と接続確立後に、同じ要求を一度だけ送ります。</summary>
    /// <returns>本文を送ったかと確認できた応答です。送信後のnullではUDPへ戻しません。</returns>
    // 抑制理由: 作成・借用した接続はfinallyのReturnが破棄または有界保持枠へ移し、所有者のDisposeAsyncで全保持接続を破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "接続はfinallyでReturnへ返却し、完全応答を確認できない接続を破棄します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "接続の所有権をfinallyで保持枠へ移し、所有者の非同期破棄まで追跡します。")]
    internal async Task<(bool Sent, WireMessage? Reply)> TryExchangeAsync(WireMessage message, CancellationToken cancellationToken = default)
    {
        if (!enabled || !PeerStreamTicket.Handles(message.Kind))
        {
            return (false, null);
        }
        PeerInfo? peer = cluster.GetPeers().FirstOrDefault(candidate => candidate.InstanceId == message.TargetInstanceId
            && string.Equals(candidate.PeerId, message.TargetId, StringComparison.Ordinal));
        if (peer is null)
        {
            return (false, null);
        }
        bool sent = false, reusable = false;
        PeerStreamConnection? connection = null;
        try
        {
            PeerStreamTicket? ticket = await ResolveAsync(peer, cancellationToken).ConfigureAwait(false);
            if (ticket is null)
            {
                return (false, null);
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            if (Math.Min(maximum, ticket.MaximumBytes) < bytes.Length)
            {
                return (false, null);
            }
            connection = Take(peer, ticket) ?? await PeerStreamConnection.OpenAsync(binding(), peer, ticket, message.InstanceId, timeout, shutdown, cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Token);
            deadline.CancelAfter(timeout);
            sent = true;
            int lane = ticket.PrioritizedFrames ? PeerStreamFrame.Lane(message.Kind)
                                                : 0;
            await PeerStreamFrame.WriteAsync(connection.Stream, bytes, lane, deadline.Token).ConfigureAwait(false);
            byte[]? response = await TcpFrame.ReadAsync(connection.Stream, "lengthPrefix", maximum, deadline.Token).ConfigureAwait(false);
            WireMessage? reply = response is null ? null
                                                  : JsonSerializer.Deserialize<WireMessage>(response);
            string replyKind = "reply";
            if (reply?.ReplyTo != message.Id || reply.InstanceId != ticket.InstanceId || reply.TargetInstanceId != message.InstanceId
                || !string.Equals(reply.SenderId, peer.PeerId, StringComparison.Ordinal) || !string.Equals(reply.TargetId, message.SenderId, StringComparison.Ordinal)
                || !string.Equals(reply.ClusterId, message.ClusterId, StringComparison.Ordinal) || !string.Equals(reply.Kind, replyKind, StringComparison.Ordinal))
            {
                return (true, null);
            }
            reusable = true;
            return (true, reply);
        }
        catch (Exception error) when ((error is SocketException or IOException or OperationCanceledException or JsonException or FormatException or InvalidOperationException)
            && (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return (sent, null);
        }
        finally
        {
            if (connection is not null)
            {
                Return(peer.PeerId, connection, reusable);
                connection = null;
            }
        }
    }

    /// <summary>同じ実体の待機接続だけを一要求へ貸し出し、切断・期限切れは送信前に閉じます。</summary>
    /// <returns>再利用できる接続、または新規接続が必要なnullです。</returns>
    // 抑制理由: 接続の所有権は呼出元へ返し、不適合な接続はusingで破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "取り出した接続は呼出元またはusingが所有します。")]
    private PeerStreamConnection? Take(PeerInfo peer, PeerStreamTicket ticket)
    {
        using (gate.EnterScope())
        {
            if (!connections.TryGetValue(peer.PeerId, out Stack<PeerStreamConnection>? available))
            {
                return null;
            }
            while (available.TryPop(out PeerStreamConnection? connection))
            {
                idleConnections--;
                bool accepted = false;
                try
                {
                    accepted = connection.Reusable(peer, ticket);
                    if (accepted)
                    {
                        return connection;
                    }
                }
                finally
                {
                    using PeerStreamConnection? discarded = accepted ? null
                                                                     : connection;
                }
            }
            connections.Remove(peer.PeerId);
            return null;
        }
    }

    /// <summary>完全応答の接続だけを最大64本まで保持し、破損したフレームを次の要求へ渡しません。</summary>
    private void Return(string peer, PeerStreamConnection connection, bool reusable)
    {
        bool retained = false;
        try
        {
            using (gate.EnterScope())
            {
                if (reusable && !closed && idleConnections < 64)
                {
                    if (!connections.TryGetValue(peer, out Stack<PeerStreamConnection>? available))
                    {
                        available = new();
                        connections.Add(peer, available);
                    }
                    connection.Returned();
                    available.Push(connection);
                    idleConnections++;
                    retained = true;
                }
            }
        }
        finally
        {
            using PeerStreamConnection? discarded = retained ? null
                                                             : connection;
        }
    }

    /// <summary>旧版が理解する参照専用要求で、一つの起動実体につき対応を一度確認します。</summary>
    /// <returns>確認済み受付、または従来配送を使うnullです。</returns>
    private async Task<PeerStreamTicket?> ResolveAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        using (gate.EnterScope())
        {
            if (observations.TryGetValue(peer.PeerId, out Observation? known) && known.InstanceId == peer.InstanceId)
            {
                return known.Ticket;
            }
        }
        ClusterRequest request = new(Guid.NewGuid(), PeerStreamTicket.Capability, peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.Null);
        WireMessage query = new(Guid.NewGuid(), cluster.LocalClusterId, cluster.LocalPeerId, cluster.LocalInstanceId, "inspect", peer.PeerId, null,
            JsonSerializer.SerializeToElement(request), peer.InstanceId);
        WireMessage? answer = await datagram(query, cancellationToken).ConfigureAwait(false);
        ClusterResponse? response = answer?.Body.Deserialize<ClusterResponse>();
        PeerStreamTicket? ticket = null;
        if (response?.Outcome == EOutcome.Succeeded && response.Value?.Json.ValueKind == JsonValueKind.Object)
        {
            ticket = response.Value.Json.Deserialize<PeerStreamTicket>();
            if (ticket is null || ticket.InstanceId != peer.InstanceId || ticket.Token == Guid.Empty || ticket.Port is < 1 or > 65535
                || ticket.MaximumBytes is < 1 or > 536870912)
            {
                return null;
            }
        }
        else
        {
            string unsupported = "Cluster.InspectionTargetUnavailable";
            if (response?.Outcome != EOutcome.NotExecuted || !string.Equals(response.Reason, unsupported, StringComparison.Ordinal))
            {
                return null;
            }
        }
        using (gate.EnterScope())
        {
            if (observations.ContainsKey(peer.PeerId) || observations.Count < 1024)
            {
                observations[peer.PeerId] = new(peer.InstanceId, ticket);
            }
        }
        return ticket;
    }

    /// <summary>Peerごとに直近実体の小さい対応情報だけを保持します。</summary>
    private sealed record Observation(Guid InstanceId, PeerStreamTicket? Ticket);

    /// <summary>送信待ちの取消と所有タスクの終了後に、受付枠を解放します。</summary>
    /// <returns>実行途中のfinallyより先にSemaphoreを破棄しない終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        using (gate.EnterScope())
        {
            closed = true;
            if (active == 0)
            {
                idle.TrySetResult();
            }
        }
        await idle.Task.ConfigureAwait(false);
        foreach (PeerStreamConnection connection in connections.Values.SelectMany(pool => pool))
        {
            connection.Dispose();
        }
        connections.Clear();
        requests.Dispose();
        controls.Dispose();
        confirmations.Dispose();
    }
}
