using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>実TCPで転送、宛先復帰、永続受付と重複除去を検証します。</summary>
public sealed class LogForwardingTests
{
    /// <summary>切断中は元ファイルを保持し、新endpointへ復帰後に未転送分を送ります。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task TcpForwardingRecoversAndDeduplicatesDurablyAcceptedEntriesAsync()
    {
        using LogTestEnvironment senderFiles = new();
        using LogTestEnvironment receiverFiles = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        WorkflowLogService sender = new(new("sender", senderFiles.Logs, InterfaceId: nic, LocalAddress: "127.0.0.1"), senderFiles.Repository, senderFiles.Secrets);
        await using ConfiguredAsyncDisposable senderLifetime = sender.ConfigureAwait(true);
        await sender.StartAsync(timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry first = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", "sender", "first");
        WorkflowLogEntry second = first with { Id = Guid.NewGuid(), Message = "while offline" };
        LogOptions receiverOptions = new("receiver", receiverFiles.Logs, InterfaceId: nic, LocalAddress: "127.0.0.1", ListenPort: 0);
        WorkflowLogService initialReceiver = new(receiverOptions, receiverFiles.Repository, receiverFiles.Secrets);
        await using (initialReceiver.ConfigureAwait(true))
        {
            await initialReceiver.StartAsync(timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Task.FromResult(initialReceiver.Statistics.ListenEndpoint is not null), timeout.Token).ConfigureAwait(true);
            int port = IPEndPoint.Parse(initialReceiver.Statistics.ListenEndpoint!).Port;
            await sender.UpdateForwardTargetsAsync([new("receiver", "127.0.0.1", port)], timeout.Token).ConfigureAwait(true);
            await sender.WriteAsync(first, timeout.Token).ConfigureAwait(true);
            await sender.FlushAsync(timeout.Token).ConfigureAwait(true);
            await WaitAsync(async () => (await initialReceiver.QueryAsync(new(Peers: ["sender"]), timeout.Token).ConfigureAwait(false)).Entries.Count == 1, timeout.Token).ConfigureAwait(true);
            await WaitAsync(() => Task.FromResult(sender.Statistics.ForwardPendingBytes == 0), timeout.Token).ConfigureAwait(true);
        }
        await sender.WriteAsync(second, timeout.Token).ConfigureAwait(true);
        await sender.FlushAsync(timeout.Token).ConfigureAwait(true);
        Assert.NotEqual(0, sender.Statistics.ForwardPendingBytes);
        Assert.NotEmpty(Directory.GetFiles(senderFiles.Logs, "*.jsonl", SearchOption.AllDirectories));
        WorkflowLogService recovered = new(receiverOptions, receiverFiles.Repository, receiverFiles.Secrets);
        await using ConfiguredAsyncDisposable recoveredLifetime = recovered.ConfigureAwait(true);
        await recovered.StartAsync(timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => Task.FromResult(recovered.Statistics.ListenEndpoint is not null), timeout.Token).ConfigureAwait(true);
        IPEndPoint endpoint = IPEndPoint.Parse(recovered.Statistics.ListenEndpoint!);
        await sender.UpdateForwardTargetsAsync([new("receiver", "127.0.0.1", endpoint.Port)], timeout.Token).ConfigureAwait(true);
        await WaitAsync(async () => (await recovered.QueryAsync(new(Peers: ["sender"]), timeout.Token).ConfigureAwait(false)).Entries.Count == 2, timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => Task.FromResult(sender.Statistics.ForwardPendingBytes == 0), timeout.Token).ConfigureAwait(true);
        using TcpClient duplicate = new(AddressFamily.InterNetwork);
        await duplicate.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = duplicate.GetStream();
        byte[] packet = JsonSerializer.SerializeToUtf8Bytes(new { SenderPeer = "sender", TargetPeer = "receiver", Entries = new[] { first, second } });
        await TcpFrame.WriteAsync(stream, packet, "lengthPrefix", timeout.Token).ConfigureAwait(true);
        byte[] ack = (await TcpFrame.ReadAsync(stream, "lengthPrefix", 65536, timeout.Token).ConfigureAwait(true))!;
        using JsonDocument document = JsonDocument.Parse(ack);
        Assert.True(document.RootElement.GetProperty("Accepted").GetBoolean());
        Assert.Equal(2, (await recovered.QueryAsync(new(Peers: ["sender"]), timeout.Token).ConfigureAwait(true)).Entries.Count);
    }

    /// <summary>後から秘密指定された既存ログも転送と検索の境界で伏せます。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ForwardingRefreshesSecretPatternsBeforeSendingStoredDataAsync()
    {
        using LogTestEnvironment senderFiles = new();
        using LogTestEnvironment receiverFiles = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        WorkflowLogService sender = new(new("sender", senderFiles.Logs, InterfaceId: nic, LocalAddress: "127.0.0.1"), senderFiles.Repository, senderFiles.Secrets);
        WorkflowLogService receiver = new(new("receiver", receiverFiles.Logs, InterfaceId: nic, LocalAddress: "127.0.0.1", ListenPort: 0), receiverFiles.Repository, receiverFiles.Secrets);
        await using ConfiguredAsyncDisposable senderLifetime = sender.ConfigureAwait(true);
        await using ConfiguredAsyncDisposable receiverLifetime = receiver.ConfigureAwait(true);
        await sender.StartAsync(timeout.Token).ConfigureAwait(true);
        await receiver.StartAsync(timeout.Token).ConfigureAwait(true);
        await sender.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "DEBUG", "sender", "future-key-123"), timeout.Token).ConfigureAwait(true);
        await sender.FlushAsync(timeout.Token).ConfigureAwait(true);
        await senderFiles.Secrets.SetAsync("api", "future-key-123", timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => Task.FromResult(receiver.Statistics.ListenEndpoint is not null), timeout.Token).ConfigureAwait(true);
        int port = IPEndPoint.Parse(receiver.Statistics.ListenEndpoint!).Port;
        await sender.UpdateForwardTargetsAsync([new("receiver", "127.0.0.1", port)], timeout.Token).ConfigureAwait(true);
        await WaitAsync(async () => (await receiver.QueryAsync(new(), timeout.Token).ConfigureAwait(false)).Entries.Count == 1, timeout.Token).ConfigureAwait(true);
        string received = JsonSerializer.Serialize(await receiver.QueryAsync(new(), timeout.Token).ConfigureAwait(true));
        Assert.DoesNotContain("future-key-123", received, StringComparison.Ordinal);
        string queried = JsonSerializer.Serialize(await sender.QueryAsync(new(), timeout.Token).ConfigureAwait(true));
        Assert.DoesNotContain("future-key-123", queried, StringComparison.Ordinal);
    }

    /// <summary>実通信の状態変化を期限内で待ちます。</summary>
    /// <returns>条件成立です。</returns>
    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken cancellationToken = default)
    {
        while (!await condition().ConfigureAwait(false))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }
}
