using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Recovery;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>旧版が残した局所失敗を、現在状態と同じ操作の確定結果でだけ補完します。</summary>
public sealed class ReceiptReconciliationTests
{
    /// <summary>別Peerの古い失敗通知が、既に受け取った確定結果の取得待ちを取り消さないことを確認します。</summary>
    /// <returns>到着順に依存せず、本文の検証後に同じ操作の成功へ補完されることです。</returns>
    [Fact]
    public async Task MatchingStaleReceiptDoesNotDiscardPendingSuccessAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(files.Root, "source", true), target = Create(files.Root, "target", true), stale = Create(files.Root, "stale", true);
        Guid operation = Guid.NewGuid();
        DurableReceipt success = await source.ApplyBatchAsync(operation, [new("workflow:test", "value", null, NodeValue.FromObject(42))]);
        DurableReceipt failure = success with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" };
        foreach (IDurableRepository store in new[] { target, stale })
        {
            Assert.True(await store.ImportAsync(new([], [failure])));
            Assert.True(await store.ImportAsync(await source.ReadCurrentStateAsync()));
        }
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        Infrastructure.Persistence.Documents.RepositorySnapshot confirmed = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Infrastructure.Persistence.Documents.RepositorySnapshot obsolete = await RecoveryRepository.Create(stale)!.ReadAsync("stale", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(confirmed));
        Assert.True(await target.ImportAsync(obsolete));
        Assert.Equal(confirmed.DeferredHistory!.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(confirmed.DeferredHistory)));
        Assert.Equal(EOutcome.Succeeded, (await target.ReadReceiptAsync(operation))!.Outcome);
        Assert.Equal(42, (await target.ReadAsync("workflow:test", "value"))!.Value!.Json.GetInt32());
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        Assert.True(await source.ImportAsync(obsolete));
        Assert.Equal(1, (await outgoing.StatusAsync()).Pending);
        Assert.True(await stale.ImportAsync(new([], [success])));
        Assert.True(await source.ImportAsync(await RecoveryRepository.Create(stale)!.ReadAsync("stale", DateTimeOffset.UtcNow.AddMinutes(2))));
        Assert.Equal(0, (await outgoing.StatusAsync()).Pending);
    }

    /// <summary>現在版が後続へ進んだ後でも、旧失敗の補完は値を再適用しません。</summary>
    /// <returns>元結果の保存、値の維持と再起動後の再発防止です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvenLegacyFailureConvergesWithoutReapplyingTheOperationAsync(bool sqlite)
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(files.Root, "source", sqlite);
        IDurableRepository target = Create(files.Root, "target", sqlite);
        Guid id = Guid.NewGuid();
        DurableReceipt committed = await source.ApplyBatchAsync(id, [new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        DurableReceipt rejected = committed with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" };
        Assert.True(await target.ImportAsync(new([], [rejected])));
        DurableEntry next = Assert.Single((await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", id.ToString("N"), NodeValue.FromObject(2))])).Entries);
        Assert.True(await target.ImportAsync(await source.ReadCurrentStateAsync()));
        Assert.True(await target.ImportAsync(new([], [committed])));
        Assert.Equal(ContractJson.Hash(committed), ContractJson.Hash(await target.ReadReceiptAsync(id)));
        Assert.Equal(next.Version, (await target.ReadAsync(next.Scope, next.Key))!.Version);
        Assert.NotNull(await target.ReadAsync(ReceiptReconciliation.AuditScope, id.ToString("N")));
        target = Create(files.Root, "target", sqlite);
        Assert.True(await target.ImportAsync(new([], [rejected])));
        Assert.Equal(EOutcome.Succeeded, (await target.ReadReceiptAsync(id))!.Outcome);
    }

    /// <summary>背景同期でも、元の成功本文ハッシュを保存して照合待ちを終了します。</summary>
    /// <returns>不一致の解消と、同じ存在証明の再取込みで待機へ戻らないことです。</returns>
    [Fact]
    public async Task DeferredLegacyReceiptCompletesAndKeepsItsOriginalHashAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(files.Root, "source", true), target = Create(files.Root, "target", true);
        Guid id = Guid.NewGuid();
        DurableReceipt committed = await source.ApplyBatchAsync(id, [new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        Assert.True(await target.ImportAsync(new([], [committed with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" }])));
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", id.ToString("N"), NodeValue.FromObject(2))]);
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        Infrastructure.Persistence.Documents.RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(snapshot.DeferredHistory!.Count, await incoming.FillAsync(await outgoing.ReadBodiesAsync(snapshot.DeferredHistory)));
        Assert.Equal(EOutcome.Succeeded, (await target.ReadReceiptAsync(id))!.Outcome);
        Assert.True(await target.ImportAsync(snapshot));
        Assert.Equal(0, (await incoming.StatusAsync()).Pending);
        Assert.Equal(2, (await target.ReadAsync("workflow:test", "value"))!.Value!.Json.GetInt32());
    }

    /// <summary>背景補完でも現在の競合を確認し、祖先だけがある旧失敗を確定成功へ変えません。</summary>
    /// <returns>未解消候補と未確認結果を維持することです。</returns>
    [Fact]
    public async Task DeferredReceiptDoesNotConfirmAcrossAnUnresolvedConflictAsync()
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(files.Root, "source", true), target = Create(files.Root, "target", true);
        Guid id = Guid.NewGuid();
        DurableReceipt committed = await source.ApplyBatchAsync(id, [new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        DurableReceipt rejected = committed with { Outcome = EOutcome.Failed, Entries = [], Reason = "Persistence.VersionConflict" };
        await target.ImportAsync(new([], [rejected]));
        await source.ApplyBatchAsync(Guid.NewGuid(), [new("workflow:test", "value", id.ToString("N"), NodeValue.FromObject(2))]);
        await target.ImportAsync(await source.ReadCurrentStateAsync());
        Assert.False(await target.ImportAsync(new([new("workflow:test", "value", Guid.NewGuid().ToString("N"), NodeValue.FromObject(3))], [])));
        RecoveryRepository outgoing = RecoveryRepository.Create(source)!, incoming = RecoveryRepository.Create(target)!;
        Infrastructure.Persistence.Documents.RepositorySnapshot snapshot = await outgoing.ReadAsync("source", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.False(await target.ImportAsync(snapshot));
        await incoming.FillAsync(await outgoing.ReadBodiesAsync(snapshot.DeferredHistory!));
        Assert.Equal(ContractJson.Hash(rejected), ContractJson.Hash(await target.ReadReceiptAsync(id)));
        Assert.Equal(2, (await target.GetConflictsAsync("workflow:test")).Count);
        Assert.True(0 < (await incoming.StatusAsync()).Pending);
        Assert.Null(await target.ReadAsync(ReceiptReconciliation.AuditScope, id.ToString("N")));
    }

    /// <summary>確定取消・別内容・現在値の証拠がない成功は、旧失敗の補正として受け入れません。</summary>
    /// <returns>結果を成功へ一律に上書きしない境界です。</returns>
    [Theory]
    [InlineData("Persistence.Aborted", false)]
    [InlineData("Persistence.VersionConflict", true)]
    [InlineData("Persistence.VersionConflict", false)]
    public async Task UnprovenOrIncompatibleSuccessDoesNotReplaceLocalResultAsync(string reason, bool differentFingerprint)
    {
        using MediaTestFiles files = new();
        IDurableRepository source = Create(files.Root, "source", true), target = Create(files.Root, "target", true);
        Guid id = Guid.NewGuid();
        DurableReceipt committed = await source.ApplyBatchAsync(id, [new("workflow:test", "value", null, NodeValue.FromObject(1))]);
        string fingerprint = committed.Fingerprint;
        if (differentFingerprint)
        {
            fingerprint = "different";
        }
        DurableReceipt rejected = committed with { Fingerprint = fingerprint, Outcome = EOutcome.Failed, Entries = [], Reason = reason };
        Assert.True(await target.ImportAsync(new([], [rejected])));
        Assert.True(await target.ImportAsync(new([], [committed])));
        Assert.Equal(ContractJson.Hash(rejected), ContractJson.Hash(await target.ReadReceiptAsync(id)));
        Assert.Null(await target.ReadAsync("workflow:test", "value"));
    }

    /// <summary>再作成可能な独立保存領域を用意します。</summary>
    /// <returns>指定した媒体の保存先です。</returns>
    private static IDurableRepository Create(string root, string name, bool sqlite)
        => sqlite ? new SqliteRepository(Path.Combine(root, name + ".db"))
                  : new JsonFileRepository(Path.Combine(root, name + ".json"));
}
