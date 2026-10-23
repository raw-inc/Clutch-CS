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

/// <summary>ZIPへの書き出しと読み込みで、設定とログが保持されることを検証します。</summary>
public sealed class ClusterArchiveFileTests
{
    private static readonly byte[] binary = [0, 7, 255];

    /// <summary>選択した場合だけ、ログの全ページがZIPへ保存されることを確認します。</summary>
    /// <returns>ZIPファイルの内容の検証。</returns>
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

    /// <summary>ログを完全に取得できなかった場合、書き出しが失敗することを確認します。</summary>
    /// <returns>ログ取得が不完全な場合のエラー処理の検証。</returns>
    [Fact]
    public async Task IncompleteLogSearchCannotProduceACompleteArchive()
    {
        using MemoryStream output = new();
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterArchiveFile.WriteAsync(output, Configuration(true),
            (_, _) => Task.FromResult(new LogQueryResult([], null, false))));
    }

    /// <summary>想定外のパス、重複したファイル名、設定と矛盾するログを含むZIPを拒否することを確認します。</summary>
    /// <returns>不正なZIPの検証。</returns>
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

    /// <summary>保存版と有効版が異なる試験用の設定を作成します。</summary>
    /// <returns>バイト列の入力値を含む設定。</returns>
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
