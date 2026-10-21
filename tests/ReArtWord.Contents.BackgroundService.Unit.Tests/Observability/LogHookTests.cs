using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>Hookへ渡すログにも、保存ログと同じ保護と因果関係を適用することを確認します。</summary>
public sealed class LogHookTests
{
    /// <summary>秘密と通常ログの個人情報を保護してから、元IDとHook履歴付きで配信します。</summary>
    /// <returns>記録と購読の実境界を通した確認。</returns>
    [Fact]
    public async Task HookReceivesOnlyRedactedLocalLogWithItsOriginalCausationAsync()
    {
        using LogTestEnvironment fixture = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await fixture.Secrets.SetAsync("api", "hook-secret-13579", timeout.Token).ConfigureAwait(true);
        WorkflowEventHub events = new("peer", Guid.NewGuid());
        WorkflowLogService log = new(new("peer", fixture.Logs), fixture.Repository, fixture.Secrets) { Events = events };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable logLifetime = log.ConfigureAwait(true);
        await log.StartAsync(timeout.Token).ConfigureAwait(true);
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        Guid causation = Guid.NewGuid();
        WorkflowLogEntry original = new(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", "peer", "hook-secret-13579",
            "flow", "node", causation, NodeValue.FromObject(new { name = "private-name" }), ContainsPersonalData: true, Hooks: [new("observer", "hook")]);
        await log.WriteAsync(original, timeout.Token).ConfigureAwait(true);
        Assert.True(await waiting.ConfigureAwait(true));
        WorkflowEvent item = reader.Current;
        Assert.Equal(original.Id, item.Id);
        Assert.Equal(causation, item.CausationId);
        Assert.Equal(original.Hooks, item.Hooks);
        Assert.DoesNotContain("hook-secret-13579", item.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-name", item.Data.Json.GetRawText(), StringComparison.Ordinal);
        WorkflowLogEntry safe = item.Data.Json.Deserialize<WorkflowLogEntry>(ContractJson.Options)!;
        Assert.Null(safe.Data);
        Assert.Equal("[personal data omitted]", safe.Message);
    }
}
