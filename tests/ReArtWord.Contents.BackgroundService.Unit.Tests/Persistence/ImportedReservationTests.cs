using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>同期がコミットより先着した場合の予約寿命を実保存先で検証します。</summary>
public sealed class ImportedReservationTests
{
    /// <summary>先着した確定内容で予約を解放し、遅延commit/abortは元の成功を返します。</summary>
    /// <returns>再起動後も次CASを妨げないことの確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImportedCommitReleasesReservationBeforeLateCompletion(bool json, bool abort)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "a", json);
        IDurableRepository second = Create(files.Root, "b", json);
        Guid operation = Guid.NewGuid();
        DurableMutation[] mutations = [new("manual-runs", "run", null, NodeValue.FromObject("started"))];
        Assert.Equal(EOutcome.Succeeded, (await first.PrepareAsync(operation, mutations).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await second.PrepareAsync(operation, mutations).ConfigureAwait(true)).Outcome);
        DurableReceipt committed = await first.CommitAsync(operation).ConfigureAwait(true);
        Assert.True(await second.ImportAsync(await first.ExportAsync().ConfigureAwait(true)).ConfigureAwait(true));
        second = Create(files.Root, "b", json);
        Assert.Equal(EOutcome.Succeeded, (await second.ReadReceiptAsync(operation).ConfigureAwait(true))!.Outcome);
        DurableReceipt next = await second.ApplyBatchAsync(Guid.NewGuid(), [new("manual-runs", "run", committed.Entries[0].Version, NodeValue.FromObject("completed"))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
        DurableReceipt late;
        if (abort)
        {
            late = await second.AbortPreparedAsync(operation).ConfigureAwait(true);
        }
        else
        {
            late = await second.CommitAsync(operation).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Succeeded, late.Outcome);
        Assert.Equal(committed.Fingerprint, late.Fingerprint);
        Assert.Equal("completed", (await second.ReadAsync("manual-runs", "run").ConfigureAwait(true))!.Value!.Json.GetString());
    }

    /// <summary>指紋不一致や未確認の成功本文では予約を安易に解除しません。</summary>
    /// <returns>read-only確認・遅延commit/abort後も予約を保全する検証です。</returns>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task UnconfirmedOrMismatchedReceiptCannotReleaseReservation(bool json, int corruption)
    {
        using MediaTestFiles files = new();
        IDurableRepository first = Create(files.Root, "a", json);
        IDurableRepository second = Create(files.Root, "b", json);
        Guid operation = Guid.NewGuid();
        DurableMutation[] mutations = [new("manual-runs", "run", null, NodeValue.FromObject("started"))];
        await first.PrepareAsync(operation, mutations).ConfigureAwait(true);
        await second.PrepareAsync(operation, mutations).ConfigureAwait(true);
        DurableReceipt committed = await first.CommitAsync(operation).ConfigureAwait(true);
        RepositorySnapshot snapshot = await first.ExportAsync().ConfigureAwait(true);
        if (corruption == 0)
        {
            snapshot = snapshot with { Receipts = [committed with { Fingerprint = "different", Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.Aborted" }] };
        }
        if (corruption == 1)
        {
            snapshot = snapshot with { Receipts = [committed with { Entries = [] }] };
        }
        if (corruption == 2)
        {
            snapshot = snapshot with { Entries = [], CausalEntries = [] };
        }
        await second.ImportAsync(snapshot).ConfigureAwait(true);
        Assert.NotNull(await second.ReadReceiptAsync(operation).ConfigureAwait(true));
        Assert.NotEqual(EOutcome.Succeeded, (await second.CommitAsync(operation).ConfigureAwait(true)).Outcome);
        Assert.NotEqual(EOutcome.Succeeded, (await second.AbortPreparedAsync(operation).ConfigureAwait(true)).Outcome);
        DurableEntry? current = await second.ReadAsync("manual-runs", "run").ConfigureAwait(true);
        DurableReceipt blocked = await second.ApplyBatchAsync(Guid.NewGuid(), [new("manual-runs", "run", current?.Version, NodeValue.FromObject("completed"))]).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, blocked.Outcome);
        Assert.Equal("Persistence.VersionConflict", blocked.Reason);
    }

    /// <summary>両媒体をそれぞれ独立した実ファイルで再作成します。</summary>
    /// <returns>製品の保存APIです。</returns>
    private static IDurableRepository Create(string root, string peer, bool json)
    {
        if (json)
        {
            return new JsonFileRepository(Path.Combine(root, peer + ".json"));
        }
        return new SqliteRepository(Path.Combine(root, peer + ".db"));
    }
}
