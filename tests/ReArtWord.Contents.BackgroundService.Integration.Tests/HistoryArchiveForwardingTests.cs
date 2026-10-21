using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Routing;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>履歴を持つ四つの実Coreで背景圧縮と通常のWF転送を同時に確認します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class HistoryArchiveForwardingTests(ITestOutputHelper output)
{
    /// <summary>対象行がある状態で起動し、圧縮を進めながら各入力の全出力を照合します。</summary>
    /// <returns>転送の遅延、欠落・重複の不在と圧縮の進捗です。</returns>
    [Fact]
    public Task BackgroundArchivePreservesWorkflowForwarding() => new ForwardingScenario(output).RunAsync(
        Environment.GetEnvironmentVariable("CONTENTS_PROBE_LABEL") ?? "archive-forwarding", ForwardingScenario.Iterations(32), historicalRecords: 10000);

    /// <summary>稼働環境を参照せず、この試験が所有する保存先だけへ過去の完了結果を置きます。</summary>
    /// <returns>Core起動前の有限なfixture生成です。</returns>
    internal static async Task SeedAsync(CorePeer peer, string id, int count, CancellationToken cancellationToken = default)
    {
        IDurableRepository repository = await RepositoryFactory.CreateAsync(Path.Combine(peer.DirectoryPath, "state"), id, new(EStorageBackend.Sqlite), cancellationToken).ConfigureAwait(false);
        for (int offset = 0; offset < count; offset += 500)
        {
            List<DurableMutation> mutations = [];
            for (int index = offset; index < Math.Min(count, offset + 500); index++)
            {
                string key = Guid.NewGuid().ToString("N");
                NodeValue value = NodeValue.FromObject(new
                {
                    Fingerprint = key,
                    InstanceId = Guid.NewGuid(),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
                    Response = new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(new { index, text = new string('x', 256) })),
                });
                mutations.Add(new("receipts:" + id, key, null, value));
            }
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations, cancellationToken).ConfigureAwait(false)).Outcome);
        }
        await File.AppendAllTextAsync(Path.Combine(peer.DirectoryPath, "contents.toml"), """

            [storage.history]
            keepDays = 0
            maxRecords = 1000
            batchRecords = 500
            """, cancellationToken).ConfigureAwait(false);
    }
}
