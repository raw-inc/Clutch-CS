using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>実HTTPと実UDPで、内側のノード期限と永続受付を確認します。</summary>
public sealed class BrowserCommandHttpTests
{
    /// <summary>HostとCoreの時計差があっても、初回操作を実行し、同じ要求の再送では重ねて実行しません。</summary>
    /// <returns>実台帳と通信経路を通した一回の処理結果です。</returns>
    [Fact]
    public Task PreparedNodeCommandKeepsItsDeadlineAcrossHttpReplayAsync() => VerifyReplayAsync(EDerivedIdFormat.Legacy);

    /// <summary>新しいURLはUUIDv8を使い、保存した受付期限・本文・対象を再送でも維持します。</summary>
    /// <returns>新形式の実HTTP・UDP経路を通した重複実行防止です。</returns>
    [Fact]
    public Task VersionedNodeCommandKeepsItsOpaqueIdAcrossHttpReplayAsync() => VerifyReplayAsync(EDerivedIdFormat.Uuid8);

    /// <summary>旧URLと新URLをそれぞれ使い、同じ元要求が一回だけ実行されることを確認します。</summary>
    /// <returns>選択した方式での通信と台帳の検証です。</returns>
    private static async Task VerifyReplayAsync(EDerivedIdFormat format)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("clutch-browser-http-");
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
            reservation.Close();
            ClusterOptions options = new(Guid.NewGuid().ToString("N"), "peer", "host", "127.0.0.1", "127.255.255.255", port, 1, true, directory.FullName)
            { HeartbeatInterval = TimeSpan.FromMilliseconds(50), PeerTimeout = TimeSpan.FromSeconds(2), RequestTimeout = TimeSpan.FromSeconds(2) };
            PeerCluster peer = new(options, new SqliteRepository(Path.Combine(directory.FullName, "state.db"))) { ReadinessCheck = _ => ValueTask.FromResult(true) };
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
            int effects = 0;
            _ = new PeerManagementEndpoint(peer, (command, _) =>
            {
                RemoteExecutionRequest request = command.Payload!.Json.Deserialize<RemoteExecutionRequest>(ContractJson.Options)!;
                Assert.Equal("nodes.execute", command.Operation);
                Assert.NotNull(command.Admission);
                Assert.False(command.Admission.ForDeadline(request.ExpiresAt).IsExpired);
                Interlocked.Increment(ref effects);
                return Task.FromResult(new ManagementReply(EOutcome.Succeeded, ContractJson.ToValue(NodeResult.Success())));
            }, _ => false, _ => false);
            await peer.StartAsync(timeout.Token).ConfigureAwait(true);
            ObserverConnection connection = new(Path.Combine(directory.FullName, "observer"));
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = connection.ConfigureAwait(true);
            string loopback = "127.0.0.1";
            ClientNetworkInterface network = ObserverNetwork.GetInterfaces().Single(item => item.Addresses.Any(address => string.Equals(address.Address, loopback, StringComparison.Ordinal)));
            await connection.SelectAsync(new(network.InterfaceId, loopback, "127.255.255.255", port), timeout.Token).ConfigureAwait(true);
            while (!connection.GetClusters().SelectMany(cluster => cluster.Peers).Any(item => item.InstanceId == peer.LocalInstanceId && item.Status == EPeerStatus.Ready))
            {
                await Task.Delay(20, timeout.Token).ConfigureAwait(true);
            }
            ShiftedClock host = new();
            ClientRequestClock clock = new(host);
            using ClientCommandLedger ledger = new(Path.Combine(directory.FullName, "ledger"), host);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(connection);
            builder.Services.AddSingleton(ledger);
            WebApplication app = builder.Build();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable appLifetime = app.ConfigureAwait(true);
            app.Urls.Add("http://127.0.0.1:0");
            app.MapGet("/api/request-clock", () => clock.Capture());
            ManagementFacade.MapNodeRoute(app, clock);
            await app.StartAsync(timeout.Token).ConfigureAwait(true);
            try
            {
                using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Add("X-Contents-Cluster", peer.LocalClusterId);
                client.DefaultRequestHeaders.Add("X-Contents-Peer", peer.LocalPeerId);
                client.DefaultRequestHeaders.Add("X-Contents-Instance", peer.LocalInstanceId.ToString("D"));
                ClientRequestClockSnapshot sample = (await client.GetFromJsonAsync<ClientRequestClockSnapshot>("/api/request-clock", ContractJson.Options, timeout.Token).ConfigureAwait(true))!;
                Guid id = Guid.NewGuid();
                RemoteExecutionRequest request = new(id, id, "manual", "r", new("node", "test", JsonSerializer.SerializeToElement(new { })),
                    new Dictionary<string, NodeValue>(), peer.LocalPeerId, peer.LocalInstanceId, sample.ObservedAt.AddMinutes(5));
                ClockedNodeCommand command = new(request, new(sample.InstanceId, sample.ElapsedTicks + TimeSpan.FromMinutes(5).Ticks, sample.ObservedAt));
                string path = "/api/nodes/execute-clocked";
                if (format == EDerivedIdFormat.Uuid8)
                {
                    path = "/api/v2/nodes/execute-clocked";
                }
                for (int index = 0; index < 2; index++)
                {
                    using HttpResponseMessage response = await client.PostAsJsonAsync(path, command, ContractJson.Options, timeout.Token).ConfigureAwait(true);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(ClientCommandLedger.Identity(id, "nodes.execute", format).ToString("D"), response.Headers.GetValues("X-Contents-Command").Single());
                    host.Adjust(4);
                }
                Assert.Equal(1, effects);
                ClientCommandRecord recorded = (await ledger.ReadAsync(ClientCommandLedger.Identity(id, "nodes.execute", format), timeout.Token).ConfigureAwait(true))!;
                Assert.Equal(request.ExpiresAt, recorded.ExpiresAt);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>経過時間を変えず、HostのUTCだけを補正します。</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        private int offset = -2;

        /// <summary>Coreとは異なる日時を返します。</summary>
        /// <returns>試験用UTC日時です。</returns>
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddHours(Volatile.Read(ref offset));

        /// <summary>実OSの日時へ影響させず、補正を与えます。</summary>
        internal void Adjust(int hours) => Interlocked.Add(ref offset, hours);
    }
}
