using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>構造付き本文も、実保存・照会で既存の秘密保護を通ります。</summary>
public sealed class StructuredLogMessageTests
{
    /// <summary>数値精度と構造を保ち、本文中の秘密項目を保存前に除去します。</summary>
    /// <returns>通常ログの実保存と再読込みの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonMessageRetainsStructureAndUsesTheSharedRedactionBoundaryAsync(bool array)
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await fixture.Secrets.SetAsync("api", "registered-secret-987", timeout.Token).ConfigureAwait(true);
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        string json = """{"address":"/audio/request","arguments":["03",9007199254740993,1.234567890123456789],"meta":{"password":"private-password","note":"registered-secret-987"}}""";
        if (array)
        {
            json = "[" + json + "]";
        }
        NodeValue message = NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(json));
        NodeResult result = await new WriteLogNode(log).ExecuteAsync(NodeTestContext.Create(extra: new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["message"] = message }), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        await log.FlushAsync(timeout.Token).ConfigureAwait(true);
        WorkflowLogEntry saved = Assert.Single((await log.QueryAsync(new(), timeout.Token).ConfigureAwait(true)).Entries);
        using JsonDocument document = JsonDocument.Parse(saved.Message);
        JsonElement packet = document.RootElement;
        if (array)
        {
            packet = Assert.Single(packet.EnumerateArray());
        }
        Assert.Equal("/audio/request", packet.GetProperty("address").GetString());
        Assert.Equal("03", packet.GetProperty("arguments")[0].GetString());
        Assert.Equal("9007199254740993", packet.GetProperty("arguments")[1].GetRawText());
        Assert.Equal("1.234567890123456789", packet.GetProperty("arguments")[2].GetRawText());
        Assert.Equal("[redacted]", packet.GetProperty("meta").GetProperty("password").GetString());
        Assert.Equal("[redacted]", packet.GetProperty("meta").GetProperty("note").GetString());
        foreach (string path in Directory.GetFiles(fixture.Logs, "*.jsonl", SearchOption.AllDirectories))
        {
            string stored = await File.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(true);
            Assert.DoesNotContain("registered-secret-987", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("private-password", stored, StringComparison.Ordinal);
        }
        Assert.Equal(json, message.Json.GetRawText());
    }

    /// <summary>JSONに似た通常テキストを記録拒否や勝手な構造変換にしません。</summary>
    /// <returns>通常テキストの保存結果です。</returns>
    [Fact]
    public async Task JsonLikeTextStillUsesNormalTextLoggingAsync()
    {
        using LogTestEnvironment fixture = new();
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets);
        await using ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync().ConfigureAwait(true);
        string message = "[received] OSC /audio/request 03";
        await log.WriteAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "INFO", "peer", message)).ConfigureAwait(true);
        await log.FlushAsync().ConfigureAwait(true);
        Assert.Equal(message, Assert.Single((await log.QueryAsync(new()).ConfigureAwait(true)).Entries).Message);
    }
}
