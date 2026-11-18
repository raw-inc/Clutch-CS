using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>履歴を失効させた保存先へ古い実Coreが再参加する経路を検証します。</summary>
public sealed class HistoryRetentionProcessTests
{
    /// <summary>実通信の再参加が、古い操作本文の復活や現在状態の巻戻りを起こしません。</summary>
    /// <returns>二つのCoreを停止・再起動した確認です。</returns>
    [Fact]
    public async Task RejoiningProcessAcceptsCurrentStateAfterHistoryExpiration()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string storage = Path.Combine(first.DirectoryPath, "state");
        IDurableRepository repository = await RepositoryFactory.CreateAsync(storage, "a", new(EStorageBackend.Sqlite)).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        Guid original = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(-10));
        await repository.ApplyBatchAsync(original, [new("workflow:retention-test", "value", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        IDurableRepository joined = await RepositoryFactory.CreateAsync(Path.Combine(second.DirectoryPath, "state"), "b", new(EStorageBackend.Sqlite)).ConfigureAwait(true);
        Assert.Equal(1, (await joined.ReadAsync("workflow:retention-test", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        await second.StopAsync().ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        DurableEntry current = (await repository.ReadAsync("workflow:retention-test", "value").ConfigureAwait(true))!;
        await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new("workflow:retention-test", "value", current.Version, NodeValue.FromObject(2))]).ConfigureAwait(true);
        await AgeFixtureAsync(Path.Combine(storage, "state.db"), original).ConfigureAwait(true);
        using (HistoryArchiveService archive = new(repository))
        {
            for (int index = 0; index < 6; index++)
            {
                await archive.MaintainAsync(new(), new(0, 1, ApplyToExisting: true), DateTimeOffset.UtcNow).ConfigureAwait(true);
            }
        }
        Assert.Equal(EOutcome.Unknown, (await repository.ReadReceiptAsync(original).ConfigureAwait(true))!.Outcome);
        CorePeer resumedFirst = new(cluster, "a", port, false);
        CorePeer resumedSecond = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable resumedFirstLifetime = resumedFirst.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable resumedSecondLifetime = resumedSecond.ConfigureAwait(true);
        await resumedFirst.StartAsync().ConfigureAwait(true);

        // 同期元の復旧後に古いピアを再参加させます。
        await CorePeer.WaitAsync(async token => (await resumedFirst.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        await resumedSecond.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await resumedFirst.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await resumedSecond.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        Assert.Equal(2, (await joined.ReadAsync("workflow:retention-test", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal(2, (await repository.ReadAsync("workflow:retention-test", "value").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Empty(await joined.GetConflictsAsync(string.Empty).ConfigureAwait(true));
    }

    /// <summary>途中参加する実Coreに退役情報を渡し、再起動後も削除済みIDと欠番を区別できることを確認します。</summary>
    /// <returns>新旧どちらの同期形式でも同じIDの再実行を防ぐことの確認です。</returns>
    [Fact]
    public async Task JoiningCoreKeepsRetiredOperationsAndAcceptsUnusedIdsAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? otherCore = Environment.GetEnvironmentVariable("CONTENTS_TEST_RETIREMENT_PEER_CORE");
        CorePeer bootstrap = new(cluster, "source", port, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable bootstrapLifetime = bootstrap.ConfigureAwait(true);
        await bootstrap.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await bootstrap.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        string storage = Path.Combine(bootstrap.DirectoryPath, "state");
        IDurableRepository seed = await RepositoryFactory.CreateAsync(storage, "source", new(EStorageBackend.Sqlite)).ConfigureAwait(true);

        // 初回のReadyは共通設定の保存完了より先に観測できます。保存途中でCoreを停止すると、
        // 再起動時は設定の復旧待ちになり、ここで調べたい履歴の再参加条件が整いません。
        await CorePeer.WaitAsync(async token => (await seed.ListAsync("cluster:configuration-operations:operational-save", token).ConfigureAwait(true))
            .Any(entry => entry.Value!.Json.TryGetProperty("Result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                && result.GetProperty("Activation").GetProperty("Outcome").Deserialize<EOutcome>() == EOutcome.Succeeded)).ConfigureAwait(true);
        await bootstrap.StopAsync().ConfigureAwait(true);
        CorePeer source = new(cluster, "source", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(true);
        CorePeer target = new(cluster, "target", port, false, executablePath: otherCore);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = target.ConfigureAwait(true);
        OperationIdSequence sequence = new();
        Guid first = sequence.Next(), hole = sequence.Next(), last = sequence.Next();
        DurableMutation[] original = [new("workflow:retirement-sync", "state", null, NodeValue.FromObject(1))];
        Assert.Equal(EOutcome.Succeeded, (await seed.ApplyBatchAsync(first, original).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await seed.ApplyBatchAsync(last, [new("workflow:retirement-sync", "state", first.ToString("N"), NodeValue.FromObject(2))]).ConfigureAwait(true)).Outcome);
        await AgeFixtureAsync(Path.Combine(storage, "state.db"), first).ConfigureAwait(true);
        using (HistoryArchiveService archive = new(seed))
        {
            for (int pass = 0; pass < 6; pass++)
            {
                await archive.MaintainAsync(new(), new(0, 1, ApplyToExisting: true), DateTimeOffset.UtcNow).ConfigureAwait(true);
            }
        }
        Assert.Equal(EOutcome.Unknown, (await seed.ReadReceiptAsync(first).ConfigureAwait(true))!.Outcome);
        Assert.NotEmpty((await seed.ExportAsync().ConfigureAwait(true)).RetiredRanges!);
        await source.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await source.GetAsync<JsonElement>("health", token).ConfigureAwait(true)).GetProperty("ready").GetBoolean()).ConfigureAwait(true);
        await WaitForCoordinatorAsync(source, "source").ConfigureAwait(true);
        await target.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await target.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2).ConfigureAwait(true);
        await target.StopAsync().ConfigureAwait(true);
        await source.StopAsync().ConfigureAwait(true);

        // 同期元を止めたまま、受信側の保存内容だけで再起動します。旧版のDBを新版で開くのは、この起動確認の後です。
        CorePeer restarted = new(cluster, "target", port, false, executablePath: otherCore);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable restartedLifetime = restarted.ConfigureAwait(true);
        await restarted.StartAsync().ConfigureAwait(true);
        await WaitForCoordinatorAsync(restarted, "target").ConfigureAwait(true);
        await restarted.StopAsync().ConfigureAwait(true);
        IDurableRepository reopened = await RepositoryFactory.CreateAsync(Path.Combine(target.DirectoryPath, "state"), "target", new(EStorageBackend.Sqlite)).ConfigureAwait(true);
        Assert.Equal(2, (await reopened.ReadAsync("workflow:retirement-sync", "state").ConfigureAwait(true))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.HistoryExpired", (await reopened.ReadReceiptAsync(first).ConfigureAwait(true))!.Reason);
        Assert.NotEqual(EOutcome.Succeeded, (await reopened.ApplyBatchAsync(first, original).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await reopened.ApplyBatchAsync(hole, [new("workflow:retirement-sync", "late", null, NodeValue.FromObject(3))]).ConfigureAwait(true)).Outcome);
    }

    /// <summary>再起動したプロセスの応答だけでなく、通常参加と担当の引継ぎまで確認します。</summary>
    /// <returns>指定したピアが担当になるまでの待機です。</returns>
    private static Task WaitForCoordinatorAsync(CorePeer peer, string expected, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token => string.Equals((await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("coordinator").GetString(), expected, StringComparison.Ordinal)
            && (await peer.GetAsync<JsonElement>("health", token).ConfigureAwait(false)).GetProperty("ready").GetBoolean(), cancellationToken);

    /// <summary>時計を進めず、隔離された試験DB内の一件だけを古い確定履歴として準備します。</summary>
    /// <returns>対象レコードの経過日数の設定です。</returns>
    private static async Task AgeFixtureAsync(string path, Guid id, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE history_candidates SET recorded_at=$old,eligible_after=0,reviewed_hash=NULL WHERE kind='receipt' AND key=$id";
        command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(true));
    }
}
