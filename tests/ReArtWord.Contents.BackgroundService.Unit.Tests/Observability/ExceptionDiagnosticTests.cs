using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>例外情報の容量上限と、永続ログの秘密値保護を検証します。</summary>
public sealed class ExceptionDiagnosticTests
{
    /// <summary>大量・巨大な内部例外と自由なDataを記録へ複製しません。</summary>
    [Fact]
    public void AggregateDiagnosticsAreBounded()
    {
        AggregateException error = new(Enumerable.Range(0, 100).Select(index => new IOException(new string('x', 100000) + index)));
        error.Data["credential"] = "not-collected";
        ExceptionDiagnostic diagnostic = ExceptionDiagnostic.Capture(error);
        Assert.True(diagnostic.Truncated);
        Assert.Equal(7, diagnostic.InnerExceptions.Count);
        string json = JsonSerializer.Serialize(diagnostic);
        Assert.True(json.Length < 32768);
        Assert.DoesNotContain("not-collected", json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 20), json, StringComparison.Ordinal);
        Assert.Null(ExceptionDiagnostic.Capture(new IOException("omit this"), false).Message);
    }

    /// <summary>別プロセスが上限外の診断を返しても共通境界で制限します。</summary>
    [Fact]
    public void ExternalDiagnosticsUseTheSameLimits()
    {
        ExceptionDiagnostic child = new("Child", new string('s', 100000), new string('s', 100000), 0, [], false);
        ExceptionDiagnostic value = new("Error", "error detail", "at script.run", 0, Enumerable.Repeat(child, 1000).ToArray(), false);
        ExceptionDiagnostic bounded = value.Bound();
        Assert.Equal(7, bounded.InnerExceptions.Count);
        Assert.True(bounded.Truncated);
        Assert.True(JsonSerializer.Serialize(bounded).Length < 32768);
    }

    /// <summary>例外本文も既存の秘密値・個人情報保護を経てディスクへ保存します。</summary>
    /// <returns>検索APIと実ファイルの保護確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredExceptionDetailsAreProtectedAsync(bool personal)
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await fixture.Secrets.SetAsync("api", "never-log-987654", timeout.Token).ConfigureAwait(true);
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        Exception error;
        try
        {
            throw new InvalidOperationException("request failed: never-log-987654", new IOException("inner never-log-987654"));
        }
        catch (InvalidOperationException caught)
        {
            error = caught;
        }
        await NodeExecutionLog.WriteExceptionAsync(NodeTestContext.Create() with { Log = log, ContainsPersonalData = personal }, "fixture.failure", error, cancellationToken: timeout.Token).ConfigureAwait(true);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        LogQueryResult result = await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry entry = Assert.Single(result.Entries);
        if (personal)
        {
            Assert.Null(entry.Data);
        }
        else
        {
            Assert.Contains("[redacted]", entry.Data!.Json.GetRawText(), StringComparison.Ordinal);
            Assert.Contains(nameof(StoredExceptionDetailsAreProtectedAsync), entry.Data.Json.GetRawText(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("never-log-987654", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        foreach (string path in Directory.GetFiles(fixture.Logs, "*.jsonl", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("never-log-987654", await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true), StringComparison.Ordinal);
        }
    }
}
