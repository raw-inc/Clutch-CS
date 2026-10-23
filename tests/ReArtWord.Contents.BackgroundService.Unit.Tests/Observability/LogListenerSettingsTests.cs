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

/// <summary>ログの待受変更と、受理済み通信の完了を実TCPで確認します。</summary>
public sealed class LogListenerSettingsTests
{
    /// <summary>接続がなくても設定変更で待受を解除し、再び有効にした宛先で受信します。</summary>
    /// <returns>待受停止、ポート解放と再開の確認です。</returns>
    [Fact]
    public async Task IdleListenerCanBeDisabledAndEnabledAsync()
    {
        using LogTestEnvironment files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogOptions initial = Options(files);
        WorkflowLogService service = new(initial, files.Repository, files.Secrets);
        await using ConfiguredAsyncDisposable lifetime = service.ConfigureAwait(true);
        await service.StartAsync(timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => service.Statistics.ListenEndpoint is not null, timeout.Token).ConfigureAwait(true);
        IPEndPoint old = IPEndPoint.Parse(service.Statistics.ListenEndpoint!);
        await service.UpdateOptionsAsync(initial with { ListenPort = null }, new(), timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => service.Statistics.ListenEndpoint is null, timeout.Token).ConfigureAwait(true);
        using (TcpListener released = new(old))
        {
            released.Start();
        }
        await service.UpdateOptionsAsync(initial, new(), timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => service.Statistics.ListenEndpoint is not null, timeout.Token).ConfigureAwait(true);
        IPEndPoint restored = IPEndPoint.Parse(service.Statistics.ListenEndpoint!);
        Assert.True(await SendAsync(restored, timeout.Token).ConfigureAwait(true));
        Assert.DoesNotContain("receive", service.Statistics.Failures!.Keys);
    }

    /// <summary>保持日数だけの変更は、既存の待受を置き換えません。</summary>
    /// <returns>同じ宛先の継続利用と設定反映の確認です。</returns>
    [Fact]
    public async Task RetentionChangeKeepsTheCurrentEndpointAsync()
    {
        using LogTestEnvironment files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogOptions initial = Options(files);
        WorkflowLogService service = new(initial, files.Repository, files.Secrets);
        await using ConfiguredAsyncDisposable lifetime = service.ConfigureAwait(true);
        await service.StartAsync(timeout.Token).ConfigureAwait(true);
        await WaitAsync(() => service.Statistics.ListenEndpoint is not null, timeout.Token).ConfigureAwait(true);
        IPEndPoint endpoint = IPEndPoint.Parse(service.Statistics.ListenEndpoint!);
        await service.UpdateOptionsAsync(initial with { Retention = [new("INFO", 8, 31)] }, new(), timeout.Token).ConfigureAwait(true);
        Assert.True(await SendAsync(endpoint, timeout.Token).ConfigureAwait(true));
        Assert.Equal(endpoint.ToString(), service.Statistics.ListenEndpoint);
        Assert.Contains(service.Options.Retention!, rule => string.Equals(rule.Level, "INFO", StringComparison.Ordinal) && rule.RawDays == 8);
    }

    /// <summary>設定変更中でも、受付済みのログを保存して応答してから待受を閉じます。</summary>
    /// <returns>受付中の一件を中断しないことの確認です。</returns>
    [Fact]
    public async Task ListenerChangeWaitsForAcceptedTransferAsync()
    {
        using LogTestEnvironment files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        LogPolicy policy = new(Options(files));
        using LogStore store = new(policy.Options, files.Repository);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogForwarder listener = new(store, new(files.Secrets), () => Volatile.Read(ref policy), async (_, token) =>
        {
            accepted.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        }, static (_, _) =>
        {
        });
        Task listening = listener.ListenAsync(timeout.Token);
        try
        {
            await WaitAsync(() => listener.Endpoint is not null, timeout.Token).ConfigureAwait(true);
            IPEndPoint endpoint = IPEndPoint.Parse(listener.Endpoint!);
            Task<bool> response = SendAsync(endpoint, timeout.Token);
            try
            {
                await accepted.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
                Volatile.Write(ref policy, new(policy.Options with { ListenPort = null }));
                listener.OptionsChanged();
                release.TrySetResult();
                Assert.True(await response.ConfigureAwait(true));
                await WaitAsync(() => listener.Endpoint is null, timeout.Token).ConfigureAwait(true);
            }
            finally
            {
                release.TrySetResult();
                await response.ConfigureAwait(true);
            }
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(true);
            try
            {
                await listening.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // 試験で開始した待受の終了を確認します。
            }
        }
    }

    /// <summary>試験専用ログ保存先をループバックへ接続します。</summary>
    /// <returns>OSが割り当てるポート番号で待ち受ける設定です。</returns>
    private static LogOptions Options(LogTestEnvironment files)
    {
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        return new("receiver", files.Logs, InterfaceId: nic, LocalAddress: "127.0.0.1", ListenPort: 0);
    }

    /// <summary>ログ一件を転送し、永続受付の応答を確認します。</summary>
    /// <returns>宛先が返した受付結果です。</returns>
    private static async Task<bool> SendAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        using TcpClient client = new(AddressFamily.InterNetwork);
        await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        WorkflowLogEntry entry = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", "sender", "listener settings test");
        byte[] packet = JsonSerializer.SerializeToUtf8Bytes(new { SenderPeer = "sender", TargetPeer = "receiver", Entries = new[] { entry } });
        await TcpFrame.WriteAsync(stream, packet, "lengthPrefix", cancellationToken).ConfigureAwait(false);
        byte[] ack = (await TcpFrame.ReadAsync(stream, "lengthPrefix", 65536, cancellationToken).ConfigureAwait(false))!;
        using JsonDocument document = JsonDocument.Parse(ack);
        return document.RootElement.GetProperty("Accepted").GetBoolean();
    }

    /// <summary>実際の待受状態が変わるまで待ちます。</summary>
    /// <returns>指定した状態の確認です。</returns>
    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }
}
