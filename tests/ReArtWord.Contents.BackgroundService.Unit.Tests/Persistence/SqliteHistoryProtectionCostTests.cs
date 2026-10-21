using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Persistence;

/// <summary>多数の保存版の保護判定で、同じ現在定義の読取りを繰り返しません。</summary>
public sealed class SqliteHistoryProtectionCostTests
{
    /// <summary>同じ取引でも現在定義の更新・削除・再作成を反映し、チェックサムの破損を隠しません。</summary>
    /// <returns>保護対象の切替と、破損時の明示失敗です。</returns>
    [Fact]
    public async Task CurrentReferencesFollowWritesWithinTheSameTransaction()
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        DurableEntry original = (await store.ApplyBatchAsync(Guid.NewGuid(), [new("workflows", "sample", null, NodeValue.FromObject(new { Revision = "v1" }))])
            .ConfigureAwait(true)).Entries.Single();
        DurableEntry first = new("workflow.history:sample", "v1", "history-one", NodeValue.FromObject(new { Revision = "v1" }));
        DurableEntry second = first with { Key = "v2" };
        using SqliteSession session = new(path);
        Assert.True(RetainedHistoryEligibility.Protected(session, first, ERetentionCategory.WorkflowVersions));
        Assert.False(RetainedHistoryEligibility.Protected(session, second, ERetentionCategory.WorkflowVersions));
        using SqliteCommand command = session.HistoryCommand();
        string payload = JsonSerializer.Serialize(original with { Value = NodeValue.FromObject(new { Wrapper = new { Revision = "v2" } }) });
        command.Parameters.AddWithValue("$key", DurableRepository.GetAddress("workflows", "sample"));
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", SqlitePayload.Hash(payload));
        command.CommandText = "UPDATE records SET payload=$payload,hash=$hash WHERE kind='entry' AND key=$key";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.False(RetainedHistoryEligibility.Protected(session, first, ERetentionCategory.WorkflowVersions));
        Assert.True(RetainedHistoryEligibility.Protected(session, second, ERetentionCategory.WorkflowVersions));
        command.CommandText = "DELETE FROM records WHERE kind='entry' AND key=$key";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.False(RetainedHistoryEligibility.Protected(session, second, ERetentionCategory.WorkflowVersions));
        command.CommandText = "INSERT INTO records(kind,key,scope,payload,hash) VALUES('entry',$key,'workflows',$payload,$hash)";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.True(RetainedHistoryEligibility.Protected(session, second, ERetentionCategory.WorkflowVersions));
        command.CommandText = "UPDATE records SET payload=payload || ' ' WHERE kind='entry' AND key=$key";
        await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        Assert.Throws<InvalidDataException>(() => RetainedHistoryEligibility.Protected(session, second, ERetentionCategory.WorkflowVersions));
    }

    /// <summary>履歴件数と現在定義の大きさが掛け合わされず、保存版・反映版の両方を保護します。</summary>
    /// <returns>共有版の更新と同期書出しにおける、実割当量と保護対象の検証です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CurrentDefinitionsAreNotReadForEveryHistoricalVersion(bool operational, bool export)
    {
        using MediaTestFiles files = new();
        string path = Path.Combine(files.Root, "state.db");
        SqliteRepository store = new(path);
        string currentScope = operational ? "cluster.operational"
                                          : "workflows";
        string activeScope = operational ? "cluster.operational.activation"
                                         : "workflow.activation";
        string key = operational ? "cluster-operational"
                                 : "sample";
        string historyScope = operational ? "cluster.operational.history"
                                          : "workflow.history:sample";
        string padding = new('x', 65536);
        List<DurableMutation> mutations = [
            new(currentScope, key, null, NodeValue.FromObject(new { Revision = "v62", Definition = padding })),
            new(activeScope, key, null, NodeValue.FromObject(new { Current = new { Revision = "v63", Definition = padding } })),
        ];
        for (int index = 0; index < 64; index++)
        {
            mutations.Add(new(historyScope, "v" + index, null, NodeValue.FromObject(new { Revision = "v" + index })));
        }
        IReadOnlyList<DurableEntry> saved = (await store.ApplyBatchAsync(Guid.NewGuid(), mutations).ConfigureAwait(true)).Entries;
        HistoryRetentionBoundary boundary = new(new Dictionary<ERetentionCategory, DateTimeOffset>(), new Dictionary<ERetentionCategory, DateTimeOffset>(),
            DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        Assert.True(await store.ImportAsync(new([], [], HistoryBoundary: boundary)).ConfigureAwait(true));
        Assert.NotNull((await store.ExportAsync().ConfigureAwait(true)).HistoryBoundary);
        _ = await store.GetSharedVersionAsync().ConfigureAwait(true);
        await store.ApplyBatchAsync(Guid.NewGuid(), [new("state", "counter", null, NodeValue.FromObject(1))]).ConfigureAwait(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RepositorySnapshot? snapshot = null;
        if (export)
        {
            using SqliteSession session = new(path, write: false);
            snapshot = SqliteSnapshot.Export(session, includeReceipts: false);
        }
        else
        {
            _ = await store.GetSharedVersionAsync().ConfigureAwait(true);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4194304);
        if (snapshot is not null)
        {
            string prefix = "[\"" + historyScope + "\",\"";
            string[] expected = saved.Where(entry => string.Equals(entry.Scope, historyScope, StringComparison.Ordinal) && entry.Key is "v62" or "v63")
                .Select(entry => DurableRepository.GetAddress(entry.Scope, entry.Key) + entry.Version).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, snapshot.ProtectedHistory!.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
    }
}
