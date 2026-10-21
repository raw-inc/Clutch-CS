using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Logs;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>ZIPの内容・検索範囲・送信量と取得量の関係を検証します。</summary>
public sealed class LogExportTests
{
    /// <summary>全ページを読み、クラスタ内だけ重複を除き、両形式の論理名と値を一致させます。</summary>
    /// <returns>分割境界を跨ぐ検索結果の確認です。</returns>
    [Fact]
    public async Task ArchiveIncludesAllPagesWithNamesTimezoneAndScopedDeduplicationAsync()
    {
        DateTimeOffset time = DateTimeOffset.Parse("2026-10-18T00:00:00Z", CultureInfo.InvariantCulture);
        WorkflowLogEntry[] entries = Enumerable.Range(0, 1205).Select(index => new WorkflowLogEntry(Guid.NewGuid(), time.AddSeconds(-index), "INFO", "writer",
            "本文 " + index + "\n次の行", "wf-" + index, "node", Data: NodeValue.FromObject(new { index, value = "詳細" }))).ToArray();
        LogQuery filter = new(From: time.AddHours(-1), Until: time, Levels: ["INFO"], Peers: ["writer"], Text: "本文", UncompressedOnly: true);
        int reads = 0;
        using MemoryStream output = new();
        (long count, bool complete) = await LogExportArchive.WriteAsync(output, new(filter, "Asia/Tokyo"), [new("a", "one"), new("a", "two"), new("b", "one")],
            (_, query, _) =>
            {
                Interlocked.Increment(ref reads);
                Assert.Equal(filter.From, query.From);
                Assert.Equal(filter.Until, query.Until);
                Assert.Equal(filter.Levels, query.Levels);
                Assert.Equal(filter.Peers, query.Peers);
                Assert.Equal(filter.Text, query.Text);
                Assert.True(query.UncompressedOnly);
                int offset = int.Parse(query.Cursor ?? "0", CultureInfo.InvariantCulture);
                string? cursor = null;
                if (offset + 25 < entries.Length)
                {
                    cursor = (offset + 25).ToString(CultureInfo.InvariantCulture);
                }
                return Task.FromResult<LogQueryResult?>(new(entries.Skip(offset).Take(25).ToArray(), cursor, true));
            }, (source, query, _) => Task.FromResult(new LogNames("WF " + source.ClusterId + " " + query.WorkflowId, "revision",
                    query.NodeIds.ToDictionary(id => id, _ => "ボタン", StringComparer.Ordinal))), IgnoreProgress);
        Assert.Equal(2410, count);
        Assert.True(complete);
        Assert.Equal(147, reads);
        output.Position = 0;
        using ZipArchive archive = new(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(8, archive.Entries.Count);
        long textCount = 0, jsonCount = 0;
        for (int part = 1; part <= 3; part++)
        {
            string stem = "logs-" + part.ToString("D5", CultureInfo.InvariantCulture);
            using StreamReader text = new(await archive.GetEntry(stem + ".txt")!.OpenAsync());
            string contents = await text.ReadToEndAsync();
            textCount += contents.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            using Stream jsonStream = await archive.GetEntry(stem + ".json")!.OpenAsync();
            using JsonDocument json = await JsonDocument.ParseAsync(jsonStream);
            JsonElement rows = json.RootElement.GetProperty("entries");
            jsonCount += rows.GetArrayLength();
            Assert.InRange(rows.GetArrayLength(), 1, 1000);
            Assert.All(rows.EnumerateArray(), row => Assert.True(row.GetProperty("namesResolved").GetBoolean()));
            if (part == 1)
            {
                Assert.Contains("2026-10-18 09:00:00.000 +09:00 [INFO] WF a wf-0 ボタン 本文 0\\n次の行", contents, StringComparison.Ordinal);
                Assert.Equal(entries[0].Message, rows[0].GetProperty("log").GetProperty("message").GetString());
                Assert.Equal("詳細", rows[0].GetProperty("log").GetProperty("data").GetProperty("json").GetProperty("value").GetString());
            }
        }
        Assert.Equal(2410, textCount);
        Assert.Equal(textCount, jsonCount);
        using Stream summaryStream = await archive.GetEntry("summary.json")!.OpenAsync();
        using JsonDocument summary = await JsonDocument.ParseAsync(summaryStream);
        Assert.Equal(0, summary.RootElement.GetProperty("nameFallbacks").GetInt64());
        Assert.True(summary.RootElement.GetProperty("complete").GetBoolean());
    }

    /// <summary>取得不能・詳細省略・削除された定義を、完全な記録と誤認させません。</summary>
    /// <returns>部分結果の明示と識別子への代替です。</returns>
    [Fact]
    public async Task ArchivePreservesOmissionsAndUnavailableSourcesAsync()
    {
        WorkflowLogEntry entry = new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, "WARN", "peer", "warning", "deleted", "node");
        string unavailable = "offline";
        using MemoryStream output = new();
        (long count, bool complete) = await LogExportArchive.WriteAsync(output, new(new()), [new("cluster", "online"), new("cluster", unavailable)],
            (source, _, _) =>
            {
                if (string.Equals(source.PeerId, unavailable, StringComparison.Ordinal))
                {
                    throw new IOException("offline");
                }
                return Task.FromResult<LogQueryResult?>(new([entry], null, true, Omissions: [new(entry.Id, ["data"], "log.details.size")]));
            }, (_, _, _) => Task.FromResult(new LogNames(null, null, new Dictionary<string, string>(StringComparer.Ordinal))), IgnoreProgress);
        Assert.Equal(1, count);
        Assert.False(complete);
        output.Position = 0;
        using ZipArchive archive = new(output, ZipArchiveMode.Read, leaveOpen: true);
        using Stream summaryStream = await archive.GetEntry("summary.json")!.OpenAsync();
        using JsonDocument summary = await JsonDocument.ParseAsync(summaryStream);
        Assert.False(summary.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, summary.RootElement.GetProperty("omittedEntries").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("nameFallbacks").GetInt64());
        Assert.Equal("cluster / offline", Assert.Single(summary.RootElement.GetProperty("unconfirmedSources").EnumerateArray()).GetString());
        using Stream stream = await archive.GetEntry("logs-00001.json")!.OpenAsync();
        using JsonDocument json = await JsonDocument.ParseAsync(stream);
        JsonElement row = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("deleted", row.GetProperty("workflowName").GetString());
        Assert.Equal("node", row.GetProperty("nodeName").GetString());
        Assert.Equal("log.details.size", row.GetProperty("omission").GetProperty("reason").GetString());
    }

    /// <summary>低速な受信側を待つ間に、次ページを先読みし続けないことを確認します。</summary>
    /// <returns>受信停止と切断が検索まで伝播する確認です。</returns>
    // 抑制理由: 取消試験はタスクを開始後に停止し、finally内で必ず完了を待ってから資源を破棄します。
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025", Justification = "finallyで取消し、書込みTaskの終了を検証してからStreamを破棄します。")]
    public async Task SlowStreamAppliesBackpressureAndCancellationAsync()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using PausingStream output = new();
        using LogExportStream stream = new(output, cancellation.Token);
        int reads = 0;
        using Task<(long Entries, bool Complete)> writing = LogExportArchive.WriteAsync(stream, new(new()), [new("c", "p")], (_, _, _) =>
        {
            Interlocked.Increment(ref reads);
            output.Pause = true;
            WorkflowLogEntry[] entries = Enumerable.Range(0, 1000).Select(index => new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(-index), "INFO", "p", "line")).ToArray();
            return Task.FromResult<LogQueryResult?>(new(entries, "next", true));
        }, (_, _, _) => throw new InvalidOperationException("No names are needed."), IgnoreProgress, cancellation.Token);
        try
        {
            await output.Blocked.Task.WaitAsync(cancellation.Token);
            Assert.Equal(1, reads);
            Assert.True(0 < output.Bytes);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writing.ConfigureAwait(true));
        }
        Assert.Equal(1, reads);
    }

    /// <summary>内容の確認だけを行う試験では画面進捗を保持しません。</summary>
    private static void IgnoreProgress(long count)
    {
    }

    /// <summary>HTTP同様に同期書込みを拒否し、書込み待ちだけを再現します。</summary>
    private sealed class PausingStream : Stream
    {
        internal bool Pause
        {
            get; set;
        }

        internal long Bytes
        {
            get; private set;
        }

        internal TaskCompletionSource Blocked
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position
        {
            get => Bytes;
            set => throw new NotSupportedException();
        }

        /// <summary>本文を保存せず、必要なら受信待機を再現します。</summary>
        /// <returns>次の送信片の受付です。</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Pause)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            Bytes += buffer.Length;
        }

        /// <summary>配列の書込みも同じ受信待機へ渡します。</summary>
        /// <returns>次の送信片の受付です。</returns>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>内部バッファを保持しないため直ちに完了します。</summary>
        /// <returns>完了済みの通知です。</returns>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>HTTP既定と同じく同期操作を拒否します。</summary>
        public override void Flush() => throw new InvalidOperationException("Synchronous HTTP IO is disabled.");

        /// <summary>HTTP既定と同じく同期書込みを拒否します。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous HTTP IO is disabled.");

        /// <summary>送信専用なので読取りを拒否します。</summary>
        /// <returns>常に例外となります。</returns>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>受信済みデータへ戻れません。</summary>
        /// <returns>常に例外となります。</returns>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>送信済みの長さを変更できません。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
