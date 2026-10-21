using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>一回限りの許可と固定NIC・Core実体に属する順序付きファイル接続です。</summary>
internal sealed class FileTransferSession : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>既存管理経路から許可された接続だけを期限付きで受け付けます。</summary>
    // 抑制理由: 開始taskはCompletionへ所有権を渡し、serviceとDisposeAsyncで回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "開始taskをCompletionで所有し必ずawaitします。")]
    internal FileTransferSession(LocalNetworkBinding binding, FileTransferPeer target, ManagementRegistry registry)
    {
        listener = new(binding.Resolve(), 0);
        bool started = false;
        try
        {
            listener.Start(1);
            started = true;
        }
        finally
        {
            if (!started)
            {
                listener.Dispose();
                lifetime.Dispose();
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        Ticket = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(secret), DateTimeOffset.UtcNow.AddSeconds(15));
        Completion = RunAsync(binding, target, registry, lifetime.Token);
    }

    /// <summary>一回だけ使用可能な短命の許可です。</summary>
    internal FileTransferTicket Ticket
    {
        get;
    }

    /// <summary>serviceが回収する通信の寿命です。</summary>
    internal Task Completion
    {
        get;
    }

    /// <summary>ファイル本文の操作を直列に実行し、任意の管理操作や確定の迂回を拒否します。</summary>
    /// <returns>接続の終了です。</returns>
    private async Task RunAsync(LocalNetworkBinding binding, FileTransferPeer target, ManagementRegistry registry, CancellationToken cancellationToken = default)
    {
        using NetworkBindingLease network = new(binding, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(network.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            byte[] supplied = new byte[32];
            await stream.ReadExactlyAsync(supplied, timeout.Token).ConfigureAwait(false);
            listener.Stop();
            if (!CryptographicOperations.FixedTimeEquals(supplied, secret))
            {
                return;
            }
            await stream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            while (!network.Token.IsCancellationRequested)
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                ManagementCommand command = await FileTransferWire.ReadAsync<ManagementCommand>(stream, timeout.Token).ConfigureAwait(false);
                if (command.RequestId == Guid.Empty || command.TargetInstanceId != target.InstanceId || command.ExpiresAt <= DateTimeOffset.UtcNow
                    || !string.Equals(command.ClusterId, target.ClusterId, StringComparison.Ordinal)
                    || !string.Equals(command.TargetPeerId, target.PeerId, StringComparison.Ordinal) || !FileTransferWire.Allows(command.Operation))
                {
                    return;
                }
                timeout.CancelAfter(TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(2).Ticks, Math.Max(1, (command.ExpiresAt - DateTimeOffset.UtcNow).Ticks))));
                ManagementReply reply = await registry.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
                await FileTransferWire.WriteAsync(stream, reply, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            // 切断した部分転送を再実行せず、この接続だけを終了します。確定は別の永続操作です。
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>通信完了を待ってから所有資源を解放します。</summary>
    /// <returns>接続の回収完了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名として接続の終了を待ちます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        listener.Dispose();
        lifetime.Dispose();
        CryptographicOperations.ZeroMemory(secret);
    }
}
