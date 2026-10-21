using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>通常停止と異なる失敗をOSへ通知してからHostの後始末を開始します。</summary>
internal static class ServiceHostFailure
{
    /// <summary>WindowsではSCMが回復設定を適用できる非ゼロ終了を先に設定します。</summary>
    internal static void Stop(IHostLifetime service, IHostApplicationLifetime application)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(application);
        if (OperatingSystem.IsWindows() && service is WindowsServiceLifetime windows)
        {
            windows.ExitCode = 1;
        }
        application.StopApplication();
    }
}
