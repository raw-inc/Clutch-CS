using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>復元ログを新しい実行イベントとして扱わず、元の識別子で保管します。</summary>
public sealed class LogArchiveRestoreTests
{
    /// <summary>再取込みでも重複せず、秘密を伏せ、Hookと転送を発火させません。</summary>
    /// <returns>実ファイル・検索・通知境界の検証です。</returns>
    [Fact]
    public async Task ImportedLogsKeepIdentityWithoutPublishingOrForwarding()
    {
        using LogTestEnvironment fixture = new();
        await fixture.Secrets.SetAsync("archive-test", "secret-archive-value");
        CapturedEvents events = new();
        WorkflowLogService log = new(new("destination", fixture.Logs), fixture.Repository, fixture.Secrets) { Events = events };
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = log.ConfigureAwait(true);
        await log.StartAsync();
        WorkflowLogEntry entry = new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-10), "INFO", "source", "secret-archive-value");
        await log.RestoreArchiveAsync([entry]);
        await log.RestoreArchiveAsync([entry]);
        LogQueryResult result = await log.QueryAsync(new());
        WorkflowLogEntry stored = Assert.Single(result.Entries);
        Assert.Equal(entry.Id, stored.Id);
        Assert.Equal(entry.Time, stored.Time);
        Assert.Equal(entry.PeerId, stored.PeerId);
        Assert.DoesNotContain("secret-archive-value", stored.Message, StringComparison.Ordinal);
        Assert.Equal(0, log.Statistics.ForwardPendingBytes);
        Assert.Equal(0, events.Published);
    }

    /// <summary>履歴の取込みでイベントが再発火したら試験を失敗させます。</summary>
    private sealed class CapturedEvents : IWorkflowEvents
    {
        /// <summary>復元処理からの配信を拒否します。</summary>
        /// <returns>この試験で呼び出してはいけません。</returns>
        public ValueTask PublishAsync(WorkflowEvent item, CancellationToken cancellationToken = default)
        {
            Published++;
            return ValueTask.CompletedTask;
        }

        /// <summary>配信された件数です。</summary>
        internal int Published
        {
            get; private set;
        }

        /// <summary>購読も必要ありません。</summary>
        /// <returns>この試験で呼び出してはいけません。</returns>
        public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
