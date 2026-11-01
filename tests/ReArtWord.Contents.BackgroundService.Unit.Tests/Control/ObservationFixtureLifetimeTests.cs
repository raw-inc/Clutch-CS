using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>通知待ち中に試験が失敗しても、元の例外を保って購読を解放します。</summary>
public sealed class ObservationFixtureLifetimeTests
{
    /// <summary>列挙子の破棄前に受信待ちを終了し、後始末の例外で試験の失敗を隠しません。</summary>
    /// <returns>元の失敗と、購読が残っていないことの確認です。</returns>
    [Fact]
    public async Task FailedTestBodyEndsThePendingReadBeforeDisposingAsync()
    {
        WorkflowEventHub events = new();
        using CancellationTokenSource cancellation = new();
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        try
        {
            Assert.Equal(1, events.Statistics.Subscribers);
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable waitingLifetime = new TestTaskLifetime(waiting, cancellation).ConfigureAwait(true);
                throw new InvalidOperationException("fixture test body failed");
            }).ConfigureAwait(true);
            Assert.Equal("fixture test body failed", failure.Message);
            Assert.True(waiting.IsCompleted);
            Assert.Equal(0, events.Statistics.Subscribers);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                await waiting.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 再現試験自身も、未完了の受信を残さず回収します。
            }
            await reader.DisposeAsync().ConfigureAwait(true);
        }
    }
}
