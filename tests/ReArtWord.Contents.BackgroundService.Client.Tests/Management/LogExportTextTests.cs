using System.Globalization;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>単一ログファイルに検索結果の全ページと詳細が残ることを検証します。</summary>
public sealed class LogExportTextTests
{
    /// <summary>検索条件を各ページへ渡し、別クラスタの同じログIDを混ぜません。</summary>
    /// <returns>条件・全件出力・時刻・原文の確認です。</returns>
    [Fact]
    public async Task TextPreservesAllPagesFiltersAndDetailedRecordsAsync()
    {
        DateTimeOffset time = DateTimeOffset.Parse("2026-10-18T00:00:00Z", CultureInfo.InvariantCulture);
        Guid causation = Guid.NewGuid();
        WorkflowLogEntry[] entries = Enumerable.Range(0, 1205).Select(index => new WorkflowLogEntry(Guid.NewGuid(), time.AddSeconds(-index), "ERROR", "writer",
            "本文 " + index + "\n次の行", "workflow", "node", causation, Data: NodeValue.FromObject(new { status = 502, detail = "例外\n診断" }),
            Raw: new("HTTP", false, 256, true))).ToArray();
        LogQuery filter = new(From: time.AddHours(-1), Until: time, Levels: ["ERROR"], Peers: ["writer"], WorkflowId: "workflow", NodeId: "node", Text: "本文",
            CausationId: causation, UncompressedOnly: false);
        using MemoryStream output = new();
        long progress = 0;
        (long count, bool complete) = await LogExportText.WriteAsync(output, new(filter, "Asia/Tokyo") { Format = ELogExportFormat.Log },
            [new("a", "one"), new("a", "two"), new("b", "one")], (_, query, _) =>
            {
                Assert.Equal(filter.From, query.From);
                Assert.Equal(filter.Until, query.Until);
                Assert.Equal(filter.Levels, query.Levels);
                Assert.Equal(filter.Peers, query.Peers);
                Assert.Equal(filter.WorkflowId, query.WorkflowId);
                Assert.Equal(filter.NodeId, query.NodeId);
                Assert.Equal(filter.Text, query.Text);
                Assert.Equal(filter.CausationId, query.CausationId);
                Assert.Equal(filter.UncompressedOnly, query.UncompressedOnly);
                int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
                string? cursor = null;
                if (offset + 25 < entries.Length)
                {
                    cursor = (offset + 25).ToString(CultureInfo.InvariantCulture);
                }
                return Task.FromResult<LogQueryResult?>(new(entries.Skip(offset).Take(25).ToArray(), cursor, true));
            }, (_, _, _) => Task.FromResult(new LogNames("確認用WF", "revision", new Dictionary<string, string>(StringComparer.Ordinal) { ["node"] = "音声生成" })),
            current => progress = current);
        Assert.True(complete);
        Assert.Equal(2410, count);
        Assert.Equal(count, progress);
        Assert.True(output.CanWrite);
        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("2026-10-18 09:00:00.000 +09:00", text, StringComparison.Ordinal);
        Assert.Contains("確認用WF / 音声生成 本文 0\\n次の行", text, StringComparison.Ordinal);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] records = lines.Where(line => line.StartsWith("  record: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2410, records.Length);
        HashSet<(string? Cluster, Guid Id)> identities = [];
        foreach (string record in records)
        {
            using JsonDocument document = JsonDocument.Parse(record[10..]);
            JsonElement row = document.RootElement;
            WorkflowLogEntry entry = row.GetProperty("log").Deserialize<WorkflowLogEntry>(ContractJson.Options)!;
            Assert.True(identities.Add((row.GetProperty("clusterId").GetString(), entry.Id)));
            Assert.Equal("確認用WF", row.GetProperty("workflowName").GetString());
            Assert.Equal(causation, entry.CausationId);
            Assert.Equal("例外\n診断", entry.Data!.Json.GetProperty("detail").GetString());
            Assert.Equal(entries[0].Raw, entry.Raw);
        }
        using JsonDocument summary = JsonDocument.Parse(lines.Single(line => line.StartsWith("# Summary: ", StringComparison.Ordinal))[11..]);
        Assert.True(summary.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(count, summary.RootElement.GetProperty("entries").GetInt64());
    }

    /// <summary>取得できないPeerと省略された詳細を、完全な出力として扱いません。</summary>
    /// <returns>ファイル内にも未確認範囲が残ることです。</returns>
    [Fact]
    public async Task TextReportsMissingSourcesAndOmittedDetailsAsync()
    {
        WorkflowLogEntry entry = new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "WARN", "peer", "warning", "deleted", "node");
        using MemoryStream output = new();
        (long count, bool complete) = await LogExportText.WriteAsync(output, new(new()) { Format = ELogExportFormat.Log },
            [new("cluster", "online"), new("cluster", "offline")], (source, _, _) =>
            {
                string offline = "offline";
                if (string.Equals(source.PeerId, offline, StringComparison.Ordinal))
                {
                    throw new IOException("offline");
                }
                return Task.FromResult<LogQueryResult?>(new([entry], null, true, Omissions: [new(entry.Id, ["data"], "log.details.size")]));
            }, (_, _, _) => Task.FromResult(new LogNames(null, null, new Dictionary<string, string>(StringComparer.Ordinal))), _ =>
            {
            });
        Assert.Equal(1, count);
        Assert.False(complete);
        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument record = JsonDocument.Parse(lines.Single(line => line.StartsWith("  record: ", StringComparison.Ordinal))[10..]);
        Assert.Equal("deleted", record.RootElement.GetProperty("workflowName").GetString());
        Assert.Equal("log.details.size", record.RootElement.GetProperty("omission").GetProperty("reason").GetString());
        using JsonDocument summary = JsonDocument.Parse(lines.Single(line => line.StartsWith("# Summary: ", StringComparison.Ordinal))[11..]);
        Assert.False(summary.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, summary.RootElement.GetProperty("omittedEntries").GetInt64());
        Assert.Equal("cluster / offline", Assert.Single(summary.RootElement.GetProperty("unconfirmedSources").EnumerateArray()).GetString());
    }
}
