using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>強制終了後に共有メモリーが残っても、確定済みの保存内容へ再接続できることを確認します。</summary>
public sealed class SqliteWalRecoveryTests
{
    /// <summary>WindowsのWAL復旧で、マッピング中のファイルを切り詰められない条件を再現します。</summary>
    /// <returns>実Coreの停止と、WALから復旧した内容の確認です。</returns>
    [Fact]
    public async Task CommittedDataSurvivesCoreTerminationWithMappedSharedMemoryAsync()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await peer.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("status").GetString() is "ready"
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, timeout.Token).ConfigureAwait(true);
        string database = Path.Combine(peer.DirectoryPath, "state", "state.db");
        SqliteRepository original = new(database);
        Assert.Equal(EOutcome.Succeeded, (await original.ApplyBatchAsync(Guid.NewGuid(), [new("sample", "confirmed", null, NodeValue.FromObject("saved before termination"))], timeout.Token).ConfigureAwait(true)).Outcome);
        DurableEntry? committed = await original.ReadAsync("sample", "confirmed", timeout.Token).ConfigureAwait(true);
        Assert.NotNull(committed);

        // 上流の回帰条件: https://github.com/sqlite/sqlite/commit/5ee733befea5
        // マッピングの残存時間を固定待機で推測せず、再接続が終わるまで明示的に保持します。
        using FileStream shared = new(database + "-shm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(shared, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        await peer.StopHostOnlyAsync(timeout.Token).ConfigureAwait(true);

        SqliteRepository recovered = new(database);
        DurableEntry? actual = await recovered.ReadAsync("sample", "confirmed", timeout.Token).ConfigureAwait(true);
        Assert.NotNull(actual);
        Assert.Equal(committed.Version, actual.Version);
        Assert.Equal("saved before termination", actual.Value!.Json.GetString());
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(timeout.Token).ConfigureAwait(true);
            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", await check.ExecuteScalarAsync(timeout.Token).ConfigureAwait(true));
        }

        // 読取り側の接続を閉じ、Core自身も残存マッピングから復旧できることを確認します。
        await peer.StartAsync(timeout.Token).ConfigureAwait(true);
    }
}
