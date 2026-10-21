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
