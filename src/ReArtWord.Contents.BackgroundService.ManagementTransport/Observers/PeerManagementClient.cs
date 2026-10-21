using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>画面の選択に依存せず、開始時のピア実体へ有限の管理要求を送ります。</summary>
public sealed class PeerManagementClient(Func<string, string, UdpObserver?> findObserver)
{
    /// <summary>本文送信前だけTCPの能力と接続を試し、未対応・未接続なら従来経路へ戻します。</summary>
    /// <returns>呼出側が解放する、一回の固定対象への通信です。</returns>
    // 抑制理由: 接続とNIC監視を返すclientへ移し、失敗時はこのメソッドで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "接続と監視の所有権をFileTransferClientへ渡します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "返却clientが接続と監視を所有し、失敗時はfinallyで解放します。")]
    public async Task<FileTransferClient> OpenFileTransferAsync(FileTransferPeer target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        UdpObserver observer = findObserver(target.ClusterId, target.PeerId) ?? throw new IOException("The peer route is unavailable.");
        ManagementCommand capability = new(Guid.NewGuid(), target.ClusterId, target.PeerId, target.InstanceId, DateTimeOffset.UtcNow.AddSeconds(10), "transfer.stream", null);
        ManagementReply available = await observer.InspectWithRetryAsync(capability, cancellationToken).ConfigureAwait(false);
        string[] unavailable = ["Management.OperationMissing", "Management.OperationModeMismatch"];
        if (available.Outcome == EOutcome.NotExecuted && unavailable.Contains(available.Reason, StringComparer.Ordinal))
        {
            return new(this, target);
        }
        if (available.Outcome != EOutcome.Succeeded || available.Value?.Json.ValueKind != JsonValueKind.True)
        {
            throw new IOException("File transfer capabilities could not be confirmed.");
        }
        Contracts.Cluster.PeerInfo peer = observer.GetClusters().Where(item => string.Equals(item.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(item => item.Peers).Single(item => item.InstanceId == target.InstanceId && string.Equals(item.PeerId, target.PeerId, StringComparison.Ordinal));
        ClientNetworkBinding selected = observer.CaptureBindingStatus().Binding;
        LocalNetworkBinding network = new(selected.InterfaceId, selected.BindAddress);
        NetworkBindingLease lease = new(network, cancellationToken);
        TcpClient? client = null;
        bool connected = false;
        try
        {
            FileTransferTicket ticket = await SendAsync<FileTransferTicket>(target, "transfer.stream.open", Guid.NewGuid(), false, cancellationToken).ConfigureAwait(false);
            if (ticket.Port is < 1 or > 65535 || ticket.Token.Length != 64 || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidDataException("Invalid file transfer ticket.");
            }
            client = new(new IPEndPoint(network.Resolve(), 0)) { NoDelay = true };
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Parse(peer.Address), ticket.Port, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Convert.FromHexString(ticket.Token), timeout.Token).ConfigureAwait(false);
            byte[] accepted = new byte[1];
            await stream.ReadExactlyAsync(accepted, timeout.Token).ConfigureAwait(false);
            if (accepted[0] != 1)
            {
                throw new InvalidDataException("File transfer ticket was not accepted.");
            }
            connected = true;
            return new(this, target, client, lease);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(this, target);
        }
        finally
        {
            if (!connected)
            {
                client?.Dispose();
                lease.Dispose();
            }
        }
    }

    /// <summary>再発見した別実体へ副作用要求を付け替えません。</summary>
    /// <returns>対象と結果を確認した型付き応答です。</returns>
    public async Task<T> SendAsync<T>(FileTransferPeer target, string operation, object payload, bool readOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        UdpObserver observer = findObserver(target.ClusterId, target.PeerId) ?? throw new IOException("The peer route is unavailable.");
        if (!observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Any(peer => peer.InstanceId == target.InstanceId && string.Equals(peer.PeerId, target.PeerId, StringComparison.Ordinal)))
        {
            throw new IOException("The peer instance changed.");
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ManagementCommand command = new(Guid.NewGuid(), target.ClusterId, target.PeerId, target.InstanceId, DateTimeOffset.UtcNow.AddMinutes(2), operation,
            NodeValue.FromJson(JsonSerializer.SerializeToElement(payload, ContractJson.Options)));
        ManagementReply reply;
        if (readOnly)
        {
            reply = await observer.InspectWithRetryAsync(command, timeout.Token).ConfigureAwait(false);
        }
        else
        {
            string commitOperation = "transfer.commit";
            if (string.Equals(operation, commitOperation, StringComparison.Ordinal))
            {
                reply = await observer.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
                if (reply.Outcome == EOutcome.Unknown)
                {
                    reply = await ConfirmFileCommitAsync(observer, command, reply, timeout.Token).ConfigureAwait(false);
                }
            }
            else
            {
                reply = await observer.NotifyAsync(command, timeout.Token).ConfigureAwait(false);
            }
        }
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException($"The peer did not confirm {operation} ({reply.Outcome}: {reply.Reason}).");
        }
        return reply.Value.Json.Deserialize<T>(ContractJson.Options) ?? throw new IOException("Invalid transfer response.");
    }

    /// <summary>長い全体検証の応答欠落は、同じ永続受付の照会で確認します。</summary>
    /// <returns>元確定処理の結果または未確認です。確定要求は再送しません。</returns>
    public static async Task<ManagementReply> ConfirmFileCommitAsync(UdpObserver observer, ManagementCommand command, ManagementReply unknown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(command);
        while (DateTimeOffset.UtcNow < command.ExpiresAt)
        {
            ManagementQueryResult result = await observer.QueryAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Reply is not null)
            {
                return result.Reply;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return unknown;
    }
}
