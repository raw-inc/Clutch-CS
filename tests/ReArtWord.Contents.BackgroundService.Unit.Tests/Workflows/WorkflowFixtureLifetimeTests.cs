using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>試験が途中で失敗しても、制限枠を保持した実行を残さないことを確認します。</summary>
public sealed class WorkflowFixtureLifetimeTests
{
    /// <summary>実行中の後続処理を止めてから、排他用の資源を破棄します。</summary>
    /// <returns>元の例外と、実行の終了・制限枠の返却です。</returns>
    [Fact]
    public async Task FailureWaitsForRunningBodyAndReturnsPermitAsync()
    {
        using ConcurrencyWorkflowTests.ProbeLimiter limiter = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = ConcurrencyWorkflowTests.Create(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, limiter);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        Task<ExecutionReport> running = fixture.Engine.ExecuteAsync(
            IterationTests.Compile(fixture, ConcurrencyWorkflowTests.Definition(true)), cancellationToken: cancellation.Token);
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = new TestTaskLifetime(running, cancellation).ConfigureAwait(true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                throw new InvalidOperationException("fixture assertion failed");
            });
            Assert.Equal("fixture assertion failed", error.Message);
            Assert.True(running.IsCompleted);
            Assert.Equal(0, limiter.Active);
            Assert.Equal(1, limiter.Released);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }
}
