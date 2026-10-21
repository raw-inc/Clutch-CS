using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>7つの独立したCore・DBを所有し、強制終了の順序と復帰条件を記録します。</summary>
internal sealed class RestartCluster : IAsyncDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly List<object> events = [];
    private readonly List<object> observations = [];
    private readonly Dictionary<int, int> processes = [];

    /// <summary>固有クラスタと試験専用ポートで7台相当を構成します。</summary>
    internal RestartCluster(int seed)
    {
        Id = "restart-" + seed.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        Port = CorePeer.AvailablePort();
        Peers = Enumerable.Range(0, 7).Select(index => new CorePeer(Id, "p" + index.ToString(CultureInfo.InvariantCulture), Port, index == 0,
            "host-" + index.ToString(CultureInfo.InvariantCulture), 1000 - index * 100)).ToArray();
    }

    /// <summary>本番と一致しないクラスタ識別です。</summary>
    internal string Id
    {
        get;
    }

    /// <summary>ループバックの発見用ポートです。</summary>
    internal int Port
    {
        get;
    }

    /// <summary>今回だけの実プロセスです。</summary>
    internal CorePeer[] Peers
    {
        get;
    }

    /// <summary>送信側が停止済みプロセスを新規選択しないための状態です。</summary>
    internal int[] Online
    {
        get;
    } = new int[7];

    /// <summary>停止・再起動の順番と実時刻です。</summary>
    internal IReadOnlyList<object> Events => events;

    /// <summary>同一プロセスの資源観測と復帰待ち時の実状態です。</summary>
    internal IReadOnlyList<object> Observations => observations;

    /// <summary>止める前のPIDを保全し、対象の所有processだけを強制終了します。</summary>
    /// <returns>完全終了の確認です。</returns>
    internal async Task StopAsync(int index, string? writingPeer, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref Online[index], 0);
        events.Add(new { action = "stop", peer = index, milliseconds = elapsed.Elapsed.TotalMilliseconds, writingPeer });
        await Peers[index].StopAsync(cancellationToken).ConfigureAwait(false);
        processes.Remove(index);
    }

    /// <summary>同じDBから新実体を起動します。全員Readyになる前にも次の相手を起動できます。</summary>
    /// <returns>新実体の管理窓口の確認です。</returns>
    internal async Task StartAsync(int index, CancellationToken cancellationToken = default)
    {
        events.Add(new { action = "start", peer = index, milliseconds = elapsed.Elapsed.TotalMilliseconds });
        await Peers[index].StartAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref Online[index], 1);
        processes[index] = (await Peers[index].GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false)).GetProperty("processId").GetInt32();
    }

    /// <summary>自然なGCとCPU・論理I/Oを、再起動で変わるPIDに紐付けて残します。</summary>
    internal void Capture(string phase)
    {
        observations.Add(new { phase, milliseconds = elapsed.Elapsed.TotalMilliseconds, samples = processes.Values.Select(ProcessResourceSample.Capture).ToArray() });
    }

    /// <summary>全員のReady、背景補完完了、同期異常なしを有限時間で待ちます。</summary>
    /// <returns>連続して確認した全Peerの状態です。</returns>
    internal async Task WaitReadyAsync(CancellationToken cancellationToken = default)
    {
        int consecutive = 0;
        await CorePeer.WaitAsync(async token =>
        {
            List<JsonElement> health = [];
            bool ready = true;
            foreach (CorePeer peer in Peers)
            {
                try
                {
                    JsonElement state = await peer.GetAsync<JsonElement>("health", token).ConfigureAwait(false);
                    health.Add(state);
                    ready &= state.GetProperty("ready").GetBoolean() && state.GetProperty("synchronizationProblem").ValueKind == JsonValueKind.Null
                        && state.GetProperty("historySynchronization").GetProperty("pending").GetInt64() == 0;
                }
                catch (InvalidOperationException error)
                {
                    ready = false;
                    health.Add(JsonSerializer.SerializeToElement(new { peer.DirectoryPath, problem = error.Message }));
                }
            }
            observations.Add(new { phase = "readiness", milliseconds = elapsed.Elapsed.TotalMilliseconds, health });
            consecutive = ready ? consecutive + 1
                                : 0;
            await Task.Delay(350, token).ConfigureAwait(false);
            return consecutive == 3;
        }, TimeSpan.FromSeconds(150), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>表示だけでなく、7DBの共有版・予約・競合集合を確認します。</summary>
    /// <returns>収束した実DBの指紋と容量です。</returns>
    internal async Task<IReadOnlyList<object>> VerifyDatabasesAsync(IReadOnlyList<RestartWorkload.Attempt> attempts, CancellationToken cancellationToken = default)
    {
        List<object> result = [];
        HashSet<string> versions = new(StringComparer.Ordinal);
        foreach (CorePeer peer in Peers)
        {
            ConflictListPage conflicts = await peer.GetAsync<ConflictListPage>("conflicts.list", cancellationToken).ConfigureAwait(false);
            Assert.Empty(conflicts.Entries);
            string path = Path.Combine(peer.DirectoryPath, "state", "state.db");
            SqliteRepository repository = new(path);
            string version = await repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false);
            versions.Add(version);
            RepositorySnapshot snapshot = await repository.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false);
            Assert.Empty(snapshot.PendingOperations ?? []);
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM records WHERE kind='prepared'";
            Assert.Equal(0L, await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            count.CommandText = "SELECT COUNT(*) FROM records WHERE kind='entry' AND scope='cluster:operations' AND json_extract(payload,'$.Value.Json.Reason') IN ('Replication.Preparing','Replication.CommitDecided','Replication.AbortDecided')";
            Assert.Equal(0L, await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            string stateWrite = "internal-data.write";
            foreach (RestartWorkload.Attempt attempt in attempts.Where(item => string.Equals(item.Command.Operation, stateWrite, StringComparison.Ordinal)))
            {
                if (attempt.Reply.Value?.Json.Deserialize<InternalDataWriteResult>(ContractJson.Options)?.Result.PersistenceOutcome == EOutcome.Succeeded)
                {
                    InternalDataWriteRequest request = attempt.Command.Payload!.Json.Deserialize<InternalDataWriteRequest>(ContractJson.Options)!;
                    InternalDataReadResult stored = await peer.GetAsync<InternalDataReadResult>("internal-data.read", request.Target, cancellationToken).ConfigureAwait(false);
                    Assert.True(stored.ConfirmedKnown);
                    Assert.Equal(ContractJson.Hash(request.Value), ContractJson.Hash(stored.ConfirmedValue));
                }
            }
            result.Add(new { path, bytes = new FileInfo(path).Length, version, current = snapshot.Entries.Count, history = snapshot.DeferredHistory?.Count ?? 0 });
        }
        Assert.Single(versions);
        return result;
    }

    /// <summary>失敗時も7つの所有processを全て回収し、DBは再現用に保存します。</summary>
    /// <returns>全子processとそのobserverの終了です。</returns>
    public async ValueTask DisposeAsync()
        => await Task.WhenAll(Peers.Select(peer => ((IAsyncDisposable)peer).DisposeAsync().AsTask())).ConfigureAwait(false);
}
