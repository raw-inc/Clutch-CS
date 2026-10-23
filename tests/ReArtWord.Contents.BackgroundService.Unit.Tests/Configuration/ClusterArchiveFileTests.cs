using System.IO.Compression;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Configuration;

/// <summary>ZIPの往復で保存版・有効版・型情報とログ選択を保持します。</summary>
public sealed class ClusterArchiveFileTests
{
    private static readonly byte[] binary = [0, 7, 255];

    /// <summary>ログを除外した場合は取得せず、含める場合はページを欠落なく保存します。</summary>
    /// <returns>実ZIPの読取り結果です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripRetainsSavedAndEffectiveDefinitionsAndOptionalLogs(bool includeLogs)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        ClusterArchiveConfiguration configuration = Configuration(includeLogs);
        WorkflowLogEntry first = new(Guid.NewGuid(), configuration.CapturedAt.AddMinutes(-1), "INFO", "screen-a", "first");
        WorkflowLogEntry second = first with { Id = Guid.NewGuid(), Message = "second" };
        int reads = 0;
        try
        {
            using (FileStream output = File.Create(path))
            {
                long count = await ClusterArchiveFile.WriteAsync(output, configuration, (query, _) =>
                {
                    Assert.Equal(configuration.CapturedAt, query.Until);
                    reads++;
                    if (reads == 1)
                    {
                        return Task.FromResult(new LogQueryResult([first], "next", true));
                    }
                    Assert.Equal("next", query.Cursor);
                    return Task.FromResult(new LogQueryResult([second], null, true));
                });
                Assert.Equal(reads, count);
            }
            (ClusterArchiveConfiguration actual, long logs) = await ClusterArchiveFile.InspectAsync(path);
            Assert.Equal(ContractJson.Hash(configuration), ContractJson.Hash(actual));
            Assert.Equal(reads, logs);
            if (includeLogs)
            {
                Assert.Equal(2, reads);
                List<WorkflowLogEntry> entries = [];
                await foreach (IReadOnlyList<WorkflowLogEntry> page in ClusterArchiveFile.ReadLogsAsync(path))
                {
                    entries.AddRange(page);
                }
                Assert.Equal(new[] { first.Id, second.Id }, entries.Select(entry => entry.Id));
            }
            else
            {
                Assert.Equal(0, reads);
            }
            ArchivedWorkflow workflow = Assert.Single(actual.Workflows);
            Assert.False(workflow.Saved.Definition.Enabled);
            Assert.True(workflow.Effective!.Enabled);
            Assert.Equal(EValueKind.Binary, workflow.Saved.Definition.Nodes[0].Inputs!["value"].Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>欠落した検索を成功したバックアップにしません。</summary>
    /// <returns>未完了ページの拒否です。</returns>
    [Fact]
    public async Task IncompleteLogSearchCannotProduceACompleteArchive()
    {
        using MemoryStream output = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterArchiveFile.WriteAsync(output, Configuration(true),
            (_, _) => Task.FromResult(new LogQueryResult([], null, false))));
    }

    /// <summary>任意パス・重複名・未宣言ログを、展開や変更より前に拒否します。</summary>
    /// <returns>ZIP構造の検査です。</returns>
    [Theory]
    [InlineData("../outside.json")]
    [InlineData("configuration.json")]
    [InlineData("logs/00000001.json")]
    public async Task UnrecognizedOrDuplicateEntriesAreRejected(string extra)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (FileStream file = File.Create(path))
            using (ZipArchive zip = new(file, ZipArchiveMode.Create))
            {
                using (Stream content = await zip.CreateEntry("configuration.json").OpenAsync())
                {
                    await JsonSerializer.SerializeAsync(content, Configuration(false), ContractJson.Options);
                }
                using Stream empty = await zip.CreateEntry(extra).OpenAsync();
                await empty.WriteAsync("[]"u8.ToArray());
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => ClusterArchiveFile.InspectAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>草稿と有効版が異なる実例を作ります。</summary>
    /// <returns>Binaryと有効状態を含む設定です。</returns>
    private static ClusterArchiveConfiguration Configuration(bool logs)
    {
        WorkflowDefinition active = new("screen", "Display", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromBytes(binary) })], [], Enabled: true);
        WorkflowDefinition draft = active with { Enabled = false };
        ClusterOperationalSettings settings = new("UTC", default, default, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        ClusterOperationalSnapshot common = new(settings, ContractJson.Hash(settings), DateTimeOffset.UtcNow);
        return new(1, DateTimeOffset.UtcNow, "studio", "screen-a", new(common, common),
            [new(new(draft, ContractJson.Hash(draft), DateTimeOffset.UtcNow, EActivationPolicy.Explicit), active)], logs);
    }
}
