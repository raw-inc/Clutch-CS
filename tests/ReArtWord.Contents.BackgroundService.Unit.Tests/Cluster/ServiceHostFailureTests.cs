using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Cluster;

/// <summary>予期しない終了だけをサービス回復対象として通知します。</summary>
public sealed class ServiceHostFailureTests
{
    /// <summary>SCMへの終了コードは停止通知の前に設定され、通常停止は0を維持します。</summary>
    [WindowsFact]
    public void WindowsFailureIsMarkedBeforeHostStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        ApplicationLifetime application = new();
        using WindowsServiceLifetime lifetime = new(new TestEnvironment(), application, NullLoggerFactory.Instance, Options.Create(new HostOptions()));
        List<int> codesAtStop = [];
        application.OnStop = () =>
        {
            if (OperatingSystem.IsWindows())
            {
                codesAtStop.Add(lifetime.ExitCode);
            }
        };
        application.StopApplication();
        ServiceHostFailure.Stop(lifetime, application);
        Assert.Equal([0, 1], codesAtStop);
    }

    /// <summary>consoleとsystemdのHostも通常の終了待機へ移し、Mainで失敗を返せます。</summary>
    [Fact]
    public void NonWindowsLifetimeStillStopsTheHost()
    {
        ApplicationLifetime application = new();
        ServiceHostFailure.Stop(new ProcessLifetime(), application);
        Assert.True(application.Stopped);
    }

    /// <summary>OSを操作しないHost停止通知です。</summary>
    private sealed class ApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public Action? OnStop
        {
            get; set;
        }

        public bool Stopped
        {
            get; private set;
        }

        /// <summary>停止時点の通知を記録します。</summary>
        public void StopApplication()
        {
            Stopped = true;
            OnStop?.Invoke();
        }
    }

    /// <summary>サービス登録を伴わないプロセスの寿命です。</summary>
    private sealed class ProcessLifetime : IHostLifetime
    {
        /// <summary>OS接続を行わず開始完了を返します。</summary>
        /// <returns>完了済みの待機です。</returns>
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>実プロセスを停止せず完了を返します。</summary>
        /// <returns>完了済みの停止です。</returns>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>WindowsServiceLifetimeを実登録なしに構築する環境です。</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName
        {
            get; set;
        } = "Testing";

        public string ApplicationName
        {
            get; set;
        } = "ContentsServiceFailureTest";

        public string ContentRootPath
        {
            get; set;
        } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider
        {
            get; set;
        } = new NullFileProvider();
    }
}
