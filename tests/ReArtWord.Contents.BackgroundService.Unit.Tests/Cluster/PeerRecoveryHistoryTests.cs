using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>復旧の現在値検証と、過去本文を実際に利用する際の検証を区別します。</summary>
public sealed class PeerRecoveryHistoryTests
{
    /// <summary>過去本文を毎回復元せず復帰し、その本文の明示照会では破損を報告します。</summary>
    /// <returns>実ハートビートによる待機からの復旧と、履歴検証の境界です。</returns>
    [Fact]
    public async Task DelayedRecoveryDoesNotReadOldReceiptBodiesAsync()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        ClusterOptions options = Options(files.Root);
        PeerCluster first = new(options, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using (first.ConfigureAwait(false))
        {
            await first.StartAsync();
            Assert.Equal(EPeerStatus.Ready, Assert.Single(first.GetPeers()).Status);
        }
        Guid operation = Guid.NewGuid();
        DurableReceipt old = await repository.ApplyBatchAsync(operation, [new("workflow:current", "value", null, NodeValue.FromObject(new string('x', 100000)))]);
        await repository.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:current", "value", old.Entries[0].Version, NodeValue.FromObject(42))]);
        Corrupt(path, "receipt", operation.ToString("N"));
        bool ready = false;
        PeerCluster recovered = new(options with { NewCluster = false }, new SqliteRepository(path))
        {
            ReadinessCheck = token => ValueTask.FromResult(Volatile.Read(ref ready)),
        };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = recovered.ConfigureAwait(false);
        await recovered.StartAsync();
        await Task.Delay(300);
        Assert.NotEqual(EPeerStatus.Ready, Assert.Single(recovered.GetPeers()).Status);
        Volatile.Write(ref ready, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (Assert.Single(recovered.GetPeers()).Status != EPeerStatus.Ready)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal(42, (await repository.ReadAsync("workflow:current", "value"))!.Value!.Json.GetInt32());
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ReadReceiptAsync(operation).AsTask());
    }

    /// <summary>現在値と未完了の操作は、過去履歴と同じ扱いで検証を後回しにしません。</summary>
    /// <returns>不整合のある復旧元をReadyにしないことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentOrPreparedCorruptionPreventsRecoveryAsync(bool prepared)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository repository = new(path);
        ClusterOptions options = Options(files.Root);
        PeerCluster first = new(options, repository) { ReadinessCheck = _ => ValueTask.FromResult(true) };
        await using (first.ConfigureAwait(false))
        {
            await first.StartAsync();
        }
        Guid operation = Guid.NewGuid();
        if (prepared)
        {
            await repository.PrepareAsync(operation, [new("workflow:current", "value", null, NodeValue.FromObject(42))]);
            Corrupt(path, "prepared", operation.ToString("N"));
        }
        else
        {
            await repository.ApplyBatchAsync(operation, [new("workflow:current", "value", null, NodeValue.FromObject(42))]);
            Corrupt(path, "entry", "[\"workflow:current\",\"value\"]");
        }
        PeerCluster recovered = new(options with { NewCluster = false }, new SqliteRepository(path));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = recovered.ConfigureAwait(false);
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.StartAsync());
        Assert.DoesNotContain(recovered.GetPeers(), peer => peer.Status == EPeerStatus.Ready);
    }

    /// <summary>他の試験や本番と交わらない通信先を確保します。</summary>
    /// <returns>短周期の隔離クラスタ設定です。</returns>
    private static ClusterOptions Options(string root)
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new(Guid.NewGuid().ToString("N"), "one", "host", "127.0.0.1", "127.255.255.255", port, 1, true, root)
        {
            PeerTimeout = TimeSpan.FromMilliseconds(150), HeartbeatInterval = TimeSpan.FromMilliseconds(25),
        };
    }

    /// <summary>照合値だけを変えて、対象一件の破損を再現します。</summary>
    private static void Corrupt(string path, string kind, string key)
    {
        using SqliteConnection connection = new("Pooling=False;Data Source=" + path);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE records SET hash=$hash WHERE kind=$kind AND key=$key";
        command.Parameters.AddWithValue("$hash", new string('0', 64));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
