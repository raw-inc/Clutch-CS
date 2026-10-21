using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>実TCPで連続転送し、誤トークン、チェックサム、切断と再送禁止を検証します。</summary>
public sealed class UpdateStreamTests
{
    /// <summary>無効接続を拒否した後も固定トークンで転送でき、同じジョブを二度受け付けません。</summary>
    /// <returns>全バイトの保存、結果照会、listener終了の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpPayloadIsVerifiedAndOneUseTicketCannotBeReopened(bool observer)
    {
        using MediaTestFiles files = new();
        byte[] bytes = RandomNumberGenerator.GetBytes(8388608);
        LocalUpdateJob job = await JobAsync(files.Root, bytes);
        string source = Path.Combine(files.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "app.dat"), bytes);
        UpdatePeerAgent agent = new(job.Options, job.PeerId, job.PreviousInstanceId, []);
        string? sourceAddress = "127.0.0.1";
        if (observer)
        {
            sourceAddress = null;
        }
        UpdateStreamReceiver receiver = new(() => agent, () => job.Options, _ => sourceAddress, job.PreviousInstanceId);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        Task serving = receiver.RunAsync(lifetime.Token);
        try
        {
            UpdateStreamCapabilities capability = receiver.Capabilities;
            Assert.Equal(1, capability.Version);
            UpdateStreamTicket ticket;
            if (observer)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.OpenAsync(new(job.OperationId, "installer"), lifetime.Token));
                ticket = await receiver.OpenAsync(job.OperationId, "127.0.0.1", lifetime.Token);
            }
            else
            {
                ticket = await receiver.OpenAsync(new(job.OperationId, "source"), lifetime.Token);
            }
            using (TcpClient invalid = new())
            {
                await invalid.ConnectAsync(IPAddress.Loopback, capability.Port, lifetime.Token);
                byte[] header = new byte[24];
                "RAWUPD01"u8.CopyTo(header);
                Guid.NewGuid().TryWriteBytes(header.AsSpan(8));
                await invalid.GetStream().WriteAsync(header, lifetime.Token);
                Assert.Equal(0, await invalid.GetStream().ReadAsync(header, lifetime.Token));
            }
            ClusterUpdateRequest request = new(job.OperationId, job.Manifest, source, [job.PeerId], EUpdateFailurePolicy.RollbackSuccessful);
            List<(long Bytes, long Total)> progress = [];
            await UpdateStreamSender.SendAsync(request, UpdateObserver.Binding(job.Options), "127.0.0.1", capability.Port, ticket, (sent, total) => progress.Add((sent, total)), lifetime.Token);
            UpdateStreamStatus status = receiver.Read(ticket.Token);
            Assert.True(status.Completed);
            Assert.Null(status.Reason);
            Assert.Equal(bytes.Length, status.Bytes);
            Assert.Contains(progress, value => 0 < value.Bytes && value.Bytes < value.Total);
            Assert.Equal(progress.Select(value => value.Bytes).Order(), progress.Select(value => value.Bytes));
            Assert.Equal((bytes.LongLength, bytes.LongLength), progress[^1]);
            await ReleaseVerifier.VerifyAsync(job.Manifest, job.PayloadRoot, lifetime.Token);
            await Assert.ThrowsAsync<IOException>(() => receiver.OpenAsync(job.OperationId, "127.0.0.1", lifetime.Token));
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(job.PayloadRoot)!, "stream-*"));
        }
        finally
        {
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        }
        Assert.Equal(0, receiver.Capabilities.Version);
    }

    /// <summary>正しいトークンでも、管理要求と異なる送信元からは転送を開始しません。</summary>
    /// <returns>送信元不一致が予約を消費しないことの確認です。</returns>
    [Fact]
    public async Task ObserverTicketRejectsAnotherSourceAddress()
    {
        using MediaTestFiles files = new();
        LocalUpdateJob job = await JobAsync(files.Root, [1]);
        UpdatePeerAgent agent = new(job.Options, job.PeerId, job.PreviousInstanceId, []);
        UpdateStreamReceiver receiver = new(() => agent, () => job.Options, _ => null, job.PreviousInstanceId);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        Task serving = receiver.RunAsync(lifetime.Token);
        try
        {
            UpdateStreamTicket ticket = await receiver.OpenAsync(job.OperationId, "127.0.0.2", lifetime.Token);
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, receiver.Capabilities.Port, lifetime.Token);
            byte[] header = new byte[24];
            "RAWUPD01"u8.CopyTo(header);
            ticket.Token.TryWriteBytes(header.AsSpan(8));
            await client.GetStream().WriteAsync(header, lifetime.Token);
            Assert.Equal(0, await client.GetStream().ReadAsync(header, lifetime.Token));
            Assert.Equal(0, receiver.Read(ticket.Token).Bytes);
            Assert.False(receiver.Read(ticket.Token).Completed);
        }
        finally
        {
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
        }
    }

    /// <summary>切断、改変、宣言外の余分なバイトを成功にしません。</summary>
    /// <returns>チェックサムと長さの障害境界です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PayloadRejectsIncompleteCorruptedAndExtraBytes(int failure)
    {
        using MediaTestFiles files = new();
        byte[] expected = [1, 2, 3];
        LocalUpdateJob job = await JobAsync(files.Root, expected);
        byte[] input = failure switch { 0 => [1, 2], 1 => [1, 2, 4], _ => [1, 2, 3, 4] };
        using MemoryStream stream = new(input);
        Exception? error = await Record.ExceptionAsync(() => UpdateStreamPayload.ReceiveAsync(job, Guid.NewGuid(), stream, _ =>
        {
        }));
        Assert.True(error is EndOfStreamException or InvalidDataException);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(job.PayloadRoot)!, "stream-*"));
        if (failure != 2)
        {
            Assert.False(File.Exists(Path.Combine(job.PayloadRoot, "app.dat")));
        }
    }

    /// <summary>実プロセスを停止しない転送専用ジョブを作成します。</summary>
    /// <returns>独立した操作rootと検証済みマニフェストです。</returns>
    private static async Task<LocalUpdateJob> JobAsync(string root, byte[] payload)
    {
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        UpdatesOptions options = new(Path.Combine(root, "installed"), Path.Combine(root, "staging"), Path.Combine(root, "backups"),
            InterfaceId: nic, LocalAddress: "127.0.0.1", ClusterId: "stream-test", BroadcastAddress: "127.255.255.255", ManagementPort: 47740);
        UpdateLayout layout = new(options, Guid.NewGuid());
        layout.CreateDirectories();
        Directory.CreateDirectory(layout.PayloadRoot);
        ReleaseManifest manifest = new("stream-test", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "app.dat", [new("app.dat", payload.Length, Convert.ToHexString(SHA256.HashData(payload)))], []);
        LocalUpdateJob job = new(Guid.Parse(Path.GetFileName(layout.OperationRoot)), "receiver", Guid.NewGuid(), ProcessOs.CurrentIdentity(), manifest, layout.PayloadRoot, options, []);
        await File.WriteAllTextAsync(layout.JobPath, JsonSerializer.Serialize(job, ContractJson.Options)).ConfigureAwait(false);
        return job;
    }
}
