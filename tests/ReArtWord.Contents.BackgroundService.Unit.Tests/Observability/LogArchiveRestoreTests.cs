using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>ログを復元しても、転送やワークフローの再実行が起きないことを検証します。</summary>
public sealed class LogArchiveRestoreTests
{
    /// <summary>重複したログの除外、秘密情報のマスク、イベントと転送の抑止を確認します。</summary>
    /// <returns>保存されたログと通知の有無の検証。</returns>
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

    /// <summary>試験中のイベント配信回数を記録します。</summary>
    private sealed class CapturedEvents : IWorkflowEvents
    {
        /// <summary>イベントの配信回数を加算します。</summary>
        /// <returns>完了済みのValueTask。</returns>
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

        /// <summary>ログの復元中にイベントが購読された場合、試験を失敗させます。</summary>
        /// <returns>呼び出された場合は試験を失敗させるため、値は返しません。</returns>
        public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
