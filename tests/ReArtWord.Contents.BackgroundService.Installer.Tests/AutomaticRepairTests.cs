using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>自動修復が実UDPで送る解消要求を検査し、異なる候補の破棄を防ぎます。</summary>
public sealed class AutomaticRepairTests
{
    /// <summary>値の本文、種類、型情報と削除状態を含め、同値の候補だけを解消します。</summary>
    /// <returns>管理経路へ送られた解消要求の検証です。</returns>
    [Theory]
    [InlineData("binary-different", false)]
    [InlineData("binary-equal", true)]
    [InlineData("binary-null", false)]
    [InlineData("json-type", false)]
    [InlineData("binary-type", false)]
    [InlineData("deleted-null", false)]
    [InlineData("deleted-equal", true)]
    [InlineData("json-order", true)]
    [InlineData("json-different", false)]
    [InlineData("hidden", false)]
    [InlineData("unresolvable", false)]
    [InlineData("empty", false)]
    public async Task ResolvesOnlyConfirmedEqualValuesAsync(string scenario, bool resolve)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string clusterId = Guid.NewGuid().ToString("N");
        (NodeValue? first, NodeValue? second) = Values(scenario);
        StateConflictCandidate[] candidates = [new("first", first), new("second", second)];
        const string empty = "empty";
        if (string.Equals(scenario, empty, StringComparison.Ordinal))
        {
            candidates = [];
        }
        StateConflict conflict = new("state", "workflow:example", "value", candidates, [], [],
            string.Equals(scenario, "hidden", StringComparison.Ordinal), !string.Equals(scenario, "unresolvable", StringComparison.Ordinal));
        ConcurrentQueue<ConflictResolveRequest> requests = new();
        Task<ManagementReply> HandleAsync(ManagementCommand command, CancellationToken token)
        {
            object response;
            switch (command.Operation)
            {
                case "conflicts.list":
                    response = new ConflictListPage([new(conflict.Id, conflict.Scope, conflict.Key, candidates.Select(candidate => candidate.Version).ToArray(), [], [], conflict.ValuesHidden, conflict.Resolvable, true)], null);
                    break;
                case "conflicts.detail":
                    response = new ConflictDetail(conflict);
                    break;
                case "conflicts.resolve":
                    requests.Enqueue(command.Payload!.Json.Deserialize<ConflictResolveRequest>(ContractJson.Options)!);
                    response = new { };
                    break;
                case "health":
                    response = new { ready = true };
                    break;
                default:
                    throw new InvalidOperationException(command.Operation);
            }
            return Task.FromResult(new ManagementReply(EOutcome.Succeeded, ContractJson.ToValue(response)));
        }
        SqliteRepository repository = new(Path.Combine(fixture.Root, "peer.db"));
        PeerCluster peer = new(new(clusterId, "peer", "host", "127.0.0.1", "127.255.255.255", port, 1, true, fixture.Root), repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        _ = new PeerManagementEndpoint(peer, HandleAsync, operation => !string.Equals(operation, "conflicts.resolve", StringComparison.Ordinal));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await peer.StartAsync(timeout.Token);
        ObservedTarget target = new(clusterId, "peer", fixture.Settings.Network with { Port = port }, Assert.Single(peer.GetPeers()), null);
        _ = await AutomaticRepair.RunAsync(target, timeout.Token);
        if (resolve)
        {
            ConflictResolveRequest request = Assert.Single(requests);
            Assert.Equal(conflict.Id, request.ConflictId);
            Assert.Equal("first", request.SelectedVersion);
            Assert.Equal(candidates.Select(candidate => candidate.Version), request.ExpectedVersions);
        }
        else
        {
            Assert.Empty(requests);
        }
    }

    /// <summary>同じJSON本文に見えても保存する意味が異なる候補を用意します。</summary>
    /// <returns>独立した二つの候補値です。</returns>
    private static (NodeValue? First, NodeValue? Second) Values(string scenario) => scenario switch
    {
        "binary-different" => (NodeValue.FromBytes(new byte[] { 1, 2 }), NodeValue.FromBytes(new byte[] { 1, 3 })),
        "binary-equal" => (NodeValue.FromBytes(new byte[] { 1, 2 }), NodeValue.FromBytes(new byte[] { 1, 2 })),
        "binary-null" => (NodeValue.FromBytes(ReadOnlyMemory<byte>.Empty), NodeValue.Null),
        "json-type" => (NodeValue.FromObject("document.txt") with { DataType = new("path") }, NodeValue.FromObject("document.txt") with { DataType = new("string") }),
        "binary-type" => (NodeValue.FromBytes(new byte[] { 1 }) with { DataType = new("binary") }, NodeValue.FromBytes(new byte[] { 1 }) with { DataType = new("any") }),
        "deleted-null" => (null, NodeValue.Null),
        "deleted-equal" => (null, null),
        "json-order" => (NodeValue.FromObject(new { a = 1, b = 2 }), NodeValue.FromObject(new { b = 2, a = 1 })),
        "json-different" => (NodeValue.FromObject(1), NodeValue.FromObject(2)),
        _ => (NodeValue.Null, NodeValue.Null),
    };
}
