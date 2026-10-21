using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>稼働状態を先に公開しても、未取得履歴が消失・再実行・現在値の巻戻りにならないことを確認します。</summary>
public sealed class StagedHistoryTests
{
    /// <summary>準備前後の許容される取消差を、次の先行同期で再取得へ戻しません。</summary>
    /// <returns>元結果を保持したまま照合結果を再利用し、再起動しても完了を維持することです。</returns>
    [Fact]
    public async Task VerifiedAbortDifferencesDoNotRequeueAcrossSynchronizationAndRestart()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        Guid id = Guid.NewGuid();
        await source.AbortPreparedAsync(id);
        await target.PrepareAsync(id, [new("workflow:test", "value", null, NodeValue.FromObject(42))]);
        await target.AbortPreparedAsync(id);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(1, (await incoming.StatusAsync()).Pending);
        Assert.Equal(1, await incoming.FillAsync(await outgoing.ReadBodiesAsync(snapshot.DeferredHistory!)));
        Assert.NotEmpty((await target.ReadReceiptAsync(id))!.Fingerprint);
        target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        incoming = RecoveryRepository.Create(target)!;
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        Assert.Equal(100, (await incoming.StatusAsync()).ProgressPercent);
    }

    /// <summary>背景保存で元のJSON表現を変更せず、後続の同期で未取得へ戻しません。</summary>
    /// <returns>異なる空白を持つ旧本文の検査値と、反復後の完了状態です。</returns>
    [Fact]
    public async Task RestoredHistoryKeepsItsOriginalByteHash()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        Guid id = Guid.NewGuid();
        await source.AbortPreparedAsync(id);
        string payload = "\n" + System.Text.Json.JsonSerializer.Serialize(await source.ReadReceiptAsync(id));
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + Path.Combine(sourceFiles.Root, "state.db")))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE records SET payload=$payload,hash=$hash WHERE kind='receipt' AND key=$id";
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(1, await incoming.FillAsync(await outgoing.ReadBodiesAsync(snapshot.DeferredHistory!)));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        using SqliteConnection restored = new("Pooling=False;Data Source=" + Path.Combine(targetFiles.Root, "state.db"));
        await restored.OpenAsync();
        using SqliteCommand verify = restored.CreateCommand();
        verify.CommandText = "SELECT hash FROM records WHERE kind='receipt' AND key=$id";
        verify.Parameters.AddWithValue("$id", id.ToString("N"));
        Assert.Equal(SqlitePayload.Hash(payload), await verify.ExecuteScalarAsync());
    }

    /// <summary>有効な保存版を先に渡し、不要な旧定義本文は存在を保持したまま後から取得します。</summary>
    /// <returns>版照合、未取得の区別と背景完了後の履歴参照です。</returns>
    [Fact]
    public async Task ReferencedWorkflowVersionRemainsCurrentWhileOlderDefinitionLoadsInBackground()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "editor", null, NodeValue.FromObject(new { Revision = "active" }))]);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow.history:editor", "active", null, NodeValue.FromObject(new { Revision = "active", SavedAt = now, Definition = "current" }))]);
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow.history:editor", "past", null, NodeValue.FromObject(new { Revision = "past", SavedAt = now.AddDays(-1), Definition = new string('x', 262144) }))]);
        Assert.True(await source.ImportAsync(new([], [], HistoryBoundary: new(new Dictionary<ERetentionCategory, DateTimeOffset>(),
            new Dictionary<ERetentionCategory, DateTimeOffset>(), DateTimeOffset.MinValue, now))));
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", now.AddMinutes(2));
        Assert.NotNull(snapshot.HistoryBoundary);
        Assert.Contains(snapshot.Entries, entry => string.Equals(entry.Scope, "workflow.history:editor", StringComparison.Ordinal) && string.Equals(entry.Key, "active", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Entries, entry => string.Equals(entry.Scope, "workflow.history:editor", StringComparison.Ordinal) && string.Equals(entry.Key, "past", StringComparison.Ordinal));
        Assert.Contains(snapshot.DeferredHistory!, reference => string.Equals(reference.Entry?.Key, "past", StringComparison.Ordinal));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(await source.GetSharedVersionAsync(), await target.GetSharedVersionAsync());
        Assert.Equal("current", (await target.ReadAsync("workflow.history:editor", "active"))!.Value!.Json.GetProperty("Definition").GetString());
        await Assert.ThrowsAsync<IOException>(() => target.ReadAsync("workflow.history:editor", "past").AsTask());
        (_, IReadOnlyList<RecoveryRecord> pending) = await incoming.PendingAsync();
        Assert.Equal(pending.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(pending)));
        Assert.Equal(262144, (await target.ReadAsync("workflow.history:editor", "past"))!.Value!.Json.GetProperty("Definition").GetString()!.Length);
        Assert.Equal(100, (await incoming.StatusAsync()).ProgressPercent);
    }

    /// <summary>再起動後も存在証明を保持し、過去本文の取得で現在値を変更しません。</summary>
    /// <returns>旧版拒否、再実行禁止、背景完了後の通常照会です。</returns>
    [Fact]
    public async Task CurrentStateIsUsableWhileHistoricalReceiptsRemainDurablyPending()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Assert.Equal(EOutcome.Succeeded, (await source.ApplyBatchAsync(first, [new("workflow:test", "value", null, NodeValue.FromObject(1))])).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await source.ApplyBatchAsync(second, [new("workflow:test", "value", first.ToString("N"), NodeValue.FromObject(2))])).Outcome);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Empty(snapshot.Receipts);
        Assert.Equal(2, snapshot.DeferredHistory!.Count);
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(2, (await target.ReadAsync("workflow:test", "value"))!.Value!.Json.GetInt32());
        Assert.Equal("Persistence.HistoryPending", (await target.ReadReceiptAsync(first))!.Reason);
        Assert.Equal(EOutcome.Unknown, (await target.ApplyBatchAsync(first, [new("workflow:test", "value", null, NodeValue.FromObject(1))])).Outcome);
        await Assert.ThrowsAsync<InvalidDataException>(() => target.ExportAsync().AsTask());
        using (SqliteConnection connection = new("Pooling=False;Data Source=" + Path.Combine(targetFiles.Root, "state.db")))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM repository_identity";
            Assert.Equal(4L, await command.ExecuteScalarAsync());
        }
        target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        RecoveryRepository incoming = RecoveryRepository.Create(target)!;
        Assert.Equal(2, (await incoming.StatusAsync()).Pending);
        Assert.Equal(0, (await incoming.StatusAsync()).ProgressPercent);
        Assert.Equal("Persistence.HistoryPending", (await target.CommitAsync(first)).Reason);
        Assert.Equal(EOutcome.Succeeded, (await target.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", second.ToString("N"), NodeValue.FromObject(3))])).Outcome);
        (string? peer, IReadOnlyList<RecoveryRecord> pending) = await incoming.PendingAsync();
        Assert.Equal("source", peer);
        Assert.Equal(2, await incoming.FillAsync(await outgoing.ReadBodiesAsync(pending)));
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        Assert.Equal(100, (await incoming.StatusAsync()).ProgressPercent);
        Assert.Equal(EOutcome.Succeeded, (await target.ReadReceiptAsync(first))!.Outcome);
        Assert.Equal(3, (await target.ReadAsync("workflow:test", "value"))!.Value!.Json.GetInt32());
        Assert.NotEmpty((await target.ExportAsync()).Receipts);
    }

    /// <summary>壊れた本文では存在証明を削除せず、後から同じ元内容で再開できます。</summary>
    /// <returns>失敗前後の未取得件数と、現在値を維持した再開です。</returns>
    [Fact]
    public async Task DamagedOrCancelledHistoryDoesNotRemoveItsReplayFence()
    {
        using MediaTestFiles sourceFiles = new();
        using MediaTestFiles targetFiles = new();
        IDurableRepository source = await RepositoryFactory.CreateAsync(sourceFiles.Root, "source", new());
        IDurableRepository target = await RepositoryFactory.CreateAsync(targetFiles.Root, "target", new());
        Guid id = Guid.NewGuid();
        await source.ApplyBatchAsync(id, [new("workflows", "saved", null, NodeValue.FromObject("definition"))]);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        IReadOnlyList<RecoveryRecordBody> bodies = await outgoing.ReadBodiesAsync(snapshot.DeferredHistory!);
        RecoveryRecordBody original = Assert.Single(bodies);
        await Assert.ThrowsAsync<InvalidDataException>(() => incoming.FillAsync([original with { Payload = "{}" }]));
        Assert.Equal(1, (await incoming.StatusAsync()).Pending);
        Assert.Equal("Persistence.HistoryPending", (await target.AbortPreparedAsync(id)).Reason);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => incoming.FillAsync(bodies, cancellation.Token));
        Assert.Equal(1, (await incoming.StatusAsync()).Pending);
        Assert.Equal(1, await incoming.FillAsync(bodies));
        Assert.Equal("definition", (await target.ReadAsync("workflows", "saved"))!.Value!.GetString());
    }
}
