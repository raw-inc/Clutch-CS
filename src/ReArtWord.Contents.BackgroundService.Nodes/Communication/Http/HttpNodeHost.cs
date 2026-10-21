using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>HTTPとWebSocketの明示NIC待受に同じHost設定を使用します。</summary>
internal static class HttpNodeHost
{
    /// <summary>受付済み処理の正常終了を待つ、呼出側所有のHostを作成します。</summary>
    /// <returns>起動・停止・破棄を呼出側が行うWebApplicationです。</returns>
    internal static WebApplication Create(LocalNetworkBinding binding, int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
        builder.WebHost.ConfigureKestrel(options => options.Listen(binding.Resolve(), port));
        return builder.Build();
    }
}
