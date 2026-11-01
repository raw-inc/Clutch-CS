using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>取得待ちと取得直後のどちらで試験が中断しても、制限枠を残さないことを確認します。</summary>
public sealed class PermitFixtureLifetimeTests
{
    /// <summary>未完了の取得を回収し、返却前に失敗した枠も解放します。</summary>
    /// <returns>待機の終了と、元の試験が持つ枠だけが残る確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureFinishesPendingAdmissionAndReturnsItsPermitAsync(bool acquired)
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        using CancellationTokenSource cancellation = new();
        WorkflowConcurrencyRequest request = new(Guid.NewGuid(), "workflow", "limit", Guid.NewGuid(), "peer", Guid.NewGuid(), 1, 0, false);
        WorkflowConcurrencyAdmission first = await limiter.AcquireAsync(request, cancellation.Token).ConfigureAwait(true);
        Assert.NotNull(first.Lease);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.Lease.ConfigureAwait(true);
        Task<WorkflowConcurrencyAdmission> pending = limiter.AcquireAsync(request with { TicketId = Guid.NewGuid() }, cancellation.Token);
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable pendingLifetime = new TestPermitLifetime(pending, cancellation).ConfigureAwait(true);
                if (acquired)
                {
                    await first.Lease.DisposeAsync().ConfigureAwait(true);
                    _ = await pending.ConfigureAwait(true);
                }
                throw new InvalidOperationException("fixture assertion failed");
            }).ConfigureAwait(true);
            Assert.Equal("fixture assertion failed", error.Message);
            Assert.True(pending.IsCompleted);
            Assert.Equal(Convert.ToInt32(!acquired), limiter.Active);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                WorkflowConcurrencyAdmission result = await pending.ConfigureAwait(true);
                if (result.Lease is not null)
                {
                    await result.Lease.DisposeAsync().ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 失敗経路の再現中も、元の待機を回収してから排他資源を破棄します。
            }
        }
    }
}
