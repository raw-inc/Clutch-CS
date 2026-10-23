using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>補助的な整理処理の障害と、Host停止時のキャンセルを区別します。</summary>
public sealed class UpdateRetentionWorkerTests
{
    /// <summary>保存形式の不正や予期しない整理失敗があってもHostを停止しません。</summary>
    /// <returns>実HostedServiceの完了とHost寿命の確認です。</returns>
    [Theory]
    [InlineData("invalid-data")]
    [InlineData("io")]
    [InlineData("unexpected")]
    [InlineData("unrelated-cancellation")]
    public async Task FailedCleanupDoesNotStopHost(string failure)
    {
        Exception exception = failure switch
        {
            "invalid-data" => new InvalidDataException("Invalid saved installation."),
            "io" => new IOException("The operation record is unavailable."),
            "unexpected" => new InvalidOperationException("Unexpected cleanup failure."),
            "unrelated-cancellation" => new OperationCanceledException("The host is still running."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        using UpdateRetentionWorker worker = new(_ => Task.FromException(exception), TimeSpan.Zero);
        using IHost host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(worker)).Build();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        try
        {
            await worker.ExecuteTask!.WaitAsync(timeout.Token);
            Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await host.StopAsync(timeout.Token);
        }
    }

    /// <summary>Host停止時は開始済みの整理をキャンセルし、その終了まで待ちます。</summary>
    /// <returns>整理を置き去りにしない停止の確認です。</returns>
    [Fact]
    public async Task HostShutdownWaitsForCleanupCancellation()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using UpdateRetentionWorker worker = new(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            finally
            {
                finished.SetResult();
            }
        }, TimeSpan.Zero);
        using IHost host = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostedService>(worker)).Build();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        await host.StopAsync(timeout.Token);
        Assert.True(finished.Task.IsCompletedSuccessfully);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }
}
