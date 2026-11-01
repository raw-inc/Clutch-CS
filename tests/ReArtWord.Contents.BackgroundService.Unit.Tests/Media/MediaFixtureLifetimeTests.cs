using System.Diagnostics;
using System.Globalization;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>試験用媒体の生成も、取消後に起動済みプロセスを残さないことを確認します。</summary>
public sealed class MediaFixtureLifetimeTests(ITestOutputHelper output)
{
    /// <summary>出力先を指定しない呼出しでも、異常終了した生成コマンドの情報を残します。</summary>
    /// <returns>終了コードと元の例外を保持した生成失敗です。</returns>
    [Fact]
    public async Task FailedFixtureGenerationKeepsCommandDiagnostics()
    {
        using ApplicationProcessFixture application = new(exitCode: 7, delayMilliseconds: 0);
        using MediaTestFiles files = new(ffmpegPath: application.Definition.ExecutablePath, ffprobePath: application.Definition.ExecutablePath);
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => files.CreateAsync("video")).ConfigureAwait(true);
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Contains("exitCode=7", failure.Message, StringComparison.Ordinal);
        Assert.Single(application.Starts());
    }

    /// <summary>開始を実際に確認してから取り消し、生成処理が戻るまでに子の終了を確認します。</summary>
    /// <returns>環境変数や本物の媒体ツールを変更しない、所有した実プロセスの回収です。</returns>
    [Fact]
    public async Task CancellingFixtureGenerationReapsItsStartedProcess()
    {
        using ApplicationProcessFixture application = new(delayMilliseconds: -1);
        using MediaTestFiles files = new(output, application.Definition.ExecutablePath, application.Definition.ExecutablePath);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource startupTimeout = new(TimeSpan.FromSeconds(20));
        Task<string> running = files.CreateAsync("video", cancellationToken: cancellation.Token);
        try
        {
            string[][] started;
            while ((started = application.Starts()).Length == 0 && !running.IsCompleted)
            {
                await Task.Delay(10, startupTimeout.Token).ConfigureAwait(true);
            }
            string[] record = Assert.Single(started);
            using Process child = Process.GetProcessById(int.Parse(record[0], CultureInfo.InvariantCulture));
            Assert.False(child.HasExited);
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(15))).ConfigureAwait(true);
            Assert.True(child.HasExited, "The cancelled fixture generation left its process running.");
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                _ = await running.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 取消結果は上で検証し、失敗時にもfixture所有の実体をDisposeで回収します。
            }
        }
    }
}
