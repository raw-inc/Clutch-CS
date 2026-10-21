using System.Net;
using System.Net.Sockets;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;

// LEGACY-FORMAT: peer-stream-v2 — RAWREQ02送信は全PeerのRAWREQ03対応確認後に撤去します。
/// <summary>一度認証した相手実体への接続を、同時に一要求だけへ貸し出します。</summary>
internal sealed class PeerStreamConnection(TcpClient client, NetworkStream stream, NetworkBindingLease binding,
    PeerStreamTicket ticket, string address) : IDisposable
{
    private long idleSince = TimeProvider.System.GetTimestamp();

    /// <summary>貸出中の要求だけが使用するフレーム用ストリームです。</summary>
    internal NetworkStream Stream => stream;

    /// <summary>同じNICの喪失とCore停止でも待機を解除します。</summary>
    internal CancellationToken Token => binding.Token;

    /// <summary>完全な応答を受けた時刻から、有限時間だけ再利用します。</summary>
    internal void Returned() => idleSince = TimeProvider.System.GetTimestamp();

    /// <summary>実体・許可・接続先が変わった接続と、閉じた接続を配送前に除外します。</summary>
    /// <returns>新しい要求を開始できる接続かです。</returns>
    internal bool Reusable(PeerInfo peer, PeerStreamTicket current)
        => ticket == current && string.Equals(address, peer.Address, StringComparison.Ordinal) && !binding.Token.IsCancellationRequested
            && TimeProvider.System.GetElapsedTime(idleSince) < TimeSpan.FromSeconds(15) && client.Connected && !client.Client.Poll(0, SelectMode.SelectRead);

    /// <summary>接続許可の確認までに失敗した資源を残さず、完了した接続だけの所有権を返します。</summary>
    /// <returns>同じPeer実体へ固定した接続です。</returns>
    // 抑制理由: 成功時は接続へ資源の所有権を移し、それ以外はfinallyのusingで全資源を解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "接続成功時に所有権を戻り値へ移し、失敗時はfinallyで破棄します。")]
    internal static async Task<PeerStreamConnection> OpenAsync(LocalNetworkBinding local, PeerInfo peer, PeerStreamTicket ticket, Guid sender,
        TimeSpan timeout, CancellationToken shutdown, CancellationToken cancellationToken = default)
    {
        TcpClient? client = null;
        NetworkStream? stream = null;
        NetworkBindingLease? binding = null;
        bool transferred = false;
        try
        {
            binding = new(local, shutdown);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, binding.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            client = new(new IPEndPoint(local.Resolve(true), 0)) { NoDelay = true };
            await client.ConnectAsync(peer.Address, ticket.Port, deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(timeout);
            stream = client.GetStream();
            byte[] header = new byte[56];
            if (ticket.PrioritizedFrames)
            {
                "RAWREQ03"u8.CopyTo(header);
            }
            else
            {
                "RAWREQ02"u8.CopyTo(header);
            }
            ticket.InstanceId.TryWriteBytes(header.AsSpan(8, 16));
            sender.TryWriteBytes(header.AsSpan(24, 16));
            ticket.Token.TryWriteBytes(header.AsSpan(40, 16));
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            byte[] accepted = new byte[1];
            await stream.ReadExactlyAsync(accepted, deadline.Token).ConfigureAwait(false);
            if (accepted[0] != 1)
            {
                throw new IOException("要求接続の許可を確認できません。");
            }
            PeerStreamConnection connection = new(client, stream, binding, ticket, peer.Address);
            transferred = true;
            return connection;
        }
        finally
        {
            using NetworkStream? failedStream = transferred ? null
                                                            : stream;
            using TcpClient? failedClient = transferred ? null
                                                        : client;
            using NetworkBindingLease? failedBinding = transferred ? null
                                                                   : binding;
        }
    }

    /// <summary>保持枠を再利用できない接続と、停止時の待機接続を閉じます。</summary>
    public void Dispose()
    {
        stream.Dispose();
        client.Dispose();
        binding.Dispose();
    }
}
