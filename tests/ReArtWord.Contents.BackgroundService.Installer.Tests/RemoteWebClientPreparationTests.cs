using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>準備完了の確認と、未対応・応答消失・異なる操作の応答を実UDPで検証します。</summary>
public sealed class RemoteWebClientPreparationTests
{
    /// <summary>未対応と確認できた場合だけ旧経路を使い、結果不明では準備を重ねません。</summary>
    /// <returns>要求内容と、実行・照会の使い分けの確認です。</returns>
    [Theory]
    [InlineData("compact")]
    [InlineData("legacy")]
    [InlineData("lost")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("legacy-foreign")]
    public async Task PreparesOnceAndFallsBackOnlyForUnsupportedCoreAsync(string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        const string legacyPrefix = "legacy", foreignSuffix = "foreign", unknownMode = "unknown", lostMode = "lost";
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, "127.0.0.1", StringComparison.Ordinal))).InterfaceId;
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        ReleaseManifest manifest = new("fixture", "win-x64", "Clutch-CS.ClientHost.exe",
            Enumerable.Range(0, 1200).Select(index => new ReleaseFile("file-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, new string('0', 64))).ToArray(), [], "fixture");
        WebClientUpdateRequest preparation = new(Guid.NewGuid(), Guid.NewGuid(), manifest);
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        target.Initialize(nic);
        UpdateObserver observer = new(binding, cluster);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
        ConcurrentDictionary<Guid, ClusterResponse> results = new();
        ConcurrentQueue<string> commands = new();
        int queries = 0;
        target.Received = async (message, _, token) =>
        {
            string queryKind = "receipt.query", requestKind = "request", compact = "setup.client.stage";
            if (string.Equals(message.Kind, queryKind, StringComparison.Ordinal))
            {
                RequestQuery query = message.Body.Deserialize<RequestQuery>()!;
                Interlocked.Increment(ref queries);
                ClusterRequestStatus status = new(false, results[query.Id]);
                await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(status)), token).ConfigureAwait(false);
                return;
            }
            if (!string.Equals(message.Kind, requestKind, StringComparison.Ordinal))
            {
                return;
            }
            ClusterRequest request = message.Body.Deserialize<ClusterRequest>()!;
            ManagementCommand command = request.Payload.Json.Deserialize<ManagementCommand>(ContractJson.Options)!;
            Assert.Equal(ContractJson.Hash(preparation), ContractJson.Hash(command.Payload!.Json.Deserialize<WebClientUpdateRequest>(ContractJson.Options)));
            ManagementReply reply;
            if (mode.StartsWith(legacyPrefix, StringComparison.Ordinal) && string.Equals(command.Operation, compact, StringComparison.Ordinal))
            {
                reply = new(EOutcome.NotExecuted, Reason: "Management.OperationMissing");
            }
            else if (mode is unknownMode)
            {
                reply = new(EOutcome.Unknown, Reason: "Management.ResultUnconfirmed");
            }
            else
            {
                Guid prepared = preparation.OperationId;
                if (mode.EndsWith(foreignSuffix, StringComparison.Ordinal))
                {
                    prepared = Guid.NewGuid();
                }
                NodeValue value = UpdateObserver.Payload(prepared);
                if (!string.Equals(command.Operation, compact, StringComparison.Ordinal))
                {
                    WebClientInstallation installation = new(preparation.InstallationId, "C:\\Fixture\\client", manifest.EntryPoint, 12345, true, true, ProgramDirectory: "C:\\Fixture\\client\\app");
                    value = UpdateObserver.Payload(new WebClientUpdateJob(prepared, installation, new(1, DateTimeOffset.UnixEpoch), manifest, "C:\\Fixture\\payload"));
                }
                reply = new(EOutcome.Succeeded, value);
            }
            ClusterResponse response = new(reply.Outcome, NodeValue.FromObject(reply), reply.Reason);
            if (results.TryAdd(request.Id, response))
            {
                commands.Enqueue(command.Operation);
            }
            if (mode is not lostMode)
            {
                await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
            }
        };
        target.Start();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<PeerInfo> found = observer.FindAsync("target", instance, timeout.Token);
        long sequence = 0;
        while (!found.IsCompleted)
        {
            PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), timeout.Token).ConfigureAwait(true);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        PeerInfo observed = await found.ConfigureAwait(true);
        if (mode.EndsWith(foreignSuffix, StringComparison.Ordinal))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => InstallerClientUpdates.PrepareRemoteAsync(observer, observed, preparation, timeout.Token));
        }
        else if (mode is unknownMode)
        {
            await Assert.ThrowsAsync<IOException>(() => InstallerClientUpdates.PrepareRemoteAsync(observer, observed, preparation, timeout.Token));
        }
        else
        {
            await InstallerClientUpdates.PrepareRemoteAsync(observer, observed, preparation, timeout.Token);
        }
        string[] expected = ["setup.client.stage"];
        if (mode.StartsWith(legacyPrefix, StringComparison.Ordinal))
        {
            expected = ["setup.client.stage", "setup.client.prepare"];
        }
        Assert.Equal(expected, commands.ToArray());
        if (mode is lostMode or unknownMode)
        {
            Assert.True(0 < queries);
        }
    }
}
