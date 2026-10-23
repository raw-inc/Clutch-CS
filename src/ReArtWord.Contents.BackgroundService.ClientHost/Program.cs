using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.ClientHost.Availability;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost;

/// <summary>Coreとは独立して終了できるBlazorクライアント配信Hostです。</summary>
internal static class Program
{
    /// <summary>GUIとAPI中継だけを起動します。</summary>
    /// <returns>正常終了は0、配信開始不能は1です。</returns>
    // 抑制理由: C#固定entrypointでHost所有資源を非同期解放し、最上位の起動障害だけを終了コードへ変換します。
    [SuppressMessage("Reliability", "CA2007", Justification = "同期contextのないentrypointでawait usingによるHost終了を確認します。"),
     SuppressMessage("Design", "CA1031", Justification = "最上位の起動失敗を秘密を含まない終了説明に変換します。"),
     SuppressMessage("SourceQuality", "RAW510", Justification = "C# Mainの固定署名にはCancellationTokenを追加できません。"),
     SuppressMessage("SourceQuality", "RAW601", Justification = "C# Mainの固定名にAsync接尾辞を付けられません。")]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            ClientHostOptions options = ClientHostOptions.Parse(args);
            Directory.CreateDirectory(options.StateDirectory);
            using FileStream instance = new(Path.Combine(options.StateDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            string serviceName = Infrastructure.Installation.WebClientProfile.ReadServiceName(options.StateDirectory) ?? "Clutch-CS Web Client";
            builder.Services.AddWindowsService(service => service.ServiceName = serviceName);
            builder.Services.AddSystemd();
            builder.WebHost.UseUrls(options.Listen.AbsoluteUri);
            builder.WebHost.UseStaticWebAssets();
            builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
            builder.Services.ConfigureHttpJsonOptions(json =>
            {
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                json.SerializerOptions.PropertyNameCaseInsensitive = false;
                json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                json.SerializerOptions.RespectNullableAnnotations = true;
                json.SerializerOptions.RespectRequiredConstructorParameters = true;
                json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });
            builder.Services.AddSingleton(_ => new ObserverConnection(options.StateDirectory));
            builder.Services.AddSingleton<DashboardCache>();
            builder.Services.AddSingleton<DashboardHostStore>();
            builder.Services.AddSingleton<Logs.LogExportJobs>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Logs.LogExportJobs>());
            builder.Services.AddSingleton<Updates.Fleet.FleetUpdatePlanner>();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(_ => new Scripting.PackageRegistryClient(Path.Combine(options.StateDirectory, "package-search")));
            builder.Services.AddSingleton<Updates.ClientUpdateLauncher>();
            builder.Services.AddHostedService(provider => new Infrastructure.Updates.Releases.UpdateRetentionWorker(provider.GetRequiredService<Updates.ClientUpdateLauncher>().TrimAsync));
            builder.Services.AddSingleton(_ => new Updates.Fleet.FleetUpdateStore(options.StateDirectory));
            builder.Services.AddSingleton<Updates.Fleet.IFleetUpdateRemote, Updates.Fleet.FleetUpdateRemote>();
            builder.Services.AddSingleton<Updates.Fleet.FleetUpdates>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Updates.Fleet.FleetUpdates>());
            builder.Services.AddSingleton<Updates.Fleet.FleetUpdateEvents>();
            builder.Services.AddSingleton<Updates.Fleet.FleetUpdateVisibility>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Updates.Fleet.FleetUpdateEvents>());
            builder.Services.AddSingleton(provider => new PeerManagementClient(provider.GetRequiredService<ObserverConnection>().FindObserver));
            builder.Services.AddSingleton<Files.SharedTransfers>();
            builder.Services.AddSingleton<Terminal.TerminalCatalog>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Files.SharedTransfers>());
            builder.Services.AddHostedService(provider => provider.GetRequiredService<DashboardCache>());
            builder.Services.AddSingleton(_ => new ClientCommandLedger(options.StateDirectory));
            builder.Services.AddSingleton<DeviceManagement.DeviceRemote>();
            builder.Services.AddSingleton<DeviceManagement.IDeviceRemote>(provider => provider.GetRequiredService<DeviceManagement.DeviceRemote>());
            builder.Services.AddSingleton<DeviceManagement.WakeHostCache>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceManagement.WakeHostCache>());
            builder.Services.AddSingleton<DeviceManagement.WakeHostDispatch>();
            builder.Services.AddSingleton<DeviceManagement.HostPowerScheduleRemote>();
            builder.Services.AddSingleton(_ => new DeviceManagement.DeviceBatchStore(options.StateDirectory));
            builder.Services.AddSingleton<DeviceManagement.DeviceBatches>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceManagement.DeviceBatches>());
            builder.Services.AddSingleton<IInternalDataRemote, ObserverInternalDataRemote>();
            builder.Services.AddSingleton(provider => new InternalDataBridge(provider.GetRequiredService<IInternalDataRemote>(), options.StateDirectory));
            await using WebApplication app = builder.Build();
            await app.Services.GetRequiredService<ObserverConnection>().InitializeAsync().ConfigureAwait(false);
            SignalScope.SignalScopeTransport.RememberPeer(app);
            ClientHttpPolicy.Configure(app);
            app.Use((HttpContext context, RequestDelegate next) => ClientErrors.HandleAsync(context, next, context.RequestAborted));
            app.UseBlazorFrameworkFiles();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            ClientRoutes.Map(app);
            DeviceManagement.DeviceRoutes.Map(app);
            Desktop.DesktopRoutes.Map(app);
            Terminal.TerminalRoutes.Map(app);
            SignalScope.SignalScopeRoutes.Map(app);
            States.StateStreamRoutes.Map(app);
            Files.BrowserFileDownloads.Map(app);
            Logs.LogExportRoutes.Map(app);
            Files.BrowserFileUploads.Map(app);
            BrandingRoutes.Map(app, options.StateDirectory);
            Updates.Fleet.FleetUpdateRoutes.Map(app);
            app.MapGet("/client/health", () => Updates.ClientUpdateLauncher.Health);
            app.MapGet("/", (IWebHostEnvironment environment) => Results.Stream(environment.WebRootFileProvider.GetFileInfo("ui-start.html").CreateReadStream(), "text/html; charset=utf-8"));
            app.MapGet("/client/transfers", (Files.SharedTransfers transfers) => transfers.Read().Jobs);
            app.MapGet("/client/transfers/state", (int? offset, Files.SharedTransfers transfers) => transfers.Read(offset ?? 0));
            app.MapGet("/client/transfers/active-count", (Files.SharedTransfers transfers) => transfers.ActiveCount);
            app.MapGet("/client/transfers/progress", (Files.SharedTransfers transfers) => transfers.Read(progress: true));
            app.MapPost("/client/transfers", (Contracts.Files.Transfers.FileTransferRequest request, Files.SharedTransfers transfers, CancellationToken cancellation) => transfers.EnqueueAsync(request, cancellation));
            app.MapGet("/client/dashboard", (HttpContext context, DashboardCache cache, Guid? after, bool? compact) =>
                cache.Read(context.Request.Headers["X-Contents-Cluster"].ToString(), context.Request.Headers["X-Contents-Peer"].ToString(), after, compact: compact == true));
            app.MapPost("/client/dashboard/batch", (Contracts.Management.Overview.DashboardSnapshotQuery[] queries, DashboardCache cache, bool? compact) => cache.ReadMany(queries, compact == true));
            app.MapGet("/client/dashboard/health", (HttpContext context, DashboardCache cache) =>
                cache.Read(context.Request.Headers["X-Contents-Cluster"].ToString(), context.Request.Headers["X-Contents-Peer"].ToString())?.Health);
            app.MapGet("/client/dashboard/status", (HttpContext context, DashboardCache cache) =>
                cache.Read(context.Request.Headers["X-Contents-Cluster"].ToString(), context.Request.Headers["X-Contents-Peer"].ToString())?.Status);
            DomainRoutes.Map(app);
            ArchiveDownloadRoutes.Map(app);
            app.MapPost("/client/script-packages/search", (Contracts.Scripting.Packages.ScriptPackageSearchRequest request, Scripting.PackageRegistryClient registry, CancellationToken cancellation) => registry.SearchAsync(request, cancellation));
            app.MapPost("/client/script-packages/details", (Contracts.Scripting.Packages.ScriptPackageDetailsRequest request, Scripting.PackageRegistryClient registry, CancellationToken cancellation) => registry.DetailsAsync(request, cancellation));
            InternalDataRoutes.Map(app);
            app.MapFallback("/api/{**path}", () => Results.NotFound());
            app.MapFallback("/client/{**path}", () => Results.NotFound());
            app.MapFallbackToFile("index.html");
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("ClientHostを起動できません。--listen <ローカルGUI URL> と --state <保存directory>、待受権限・配布資材を確認してください。").ConfigureAwait(false);
            return 1;
        }
    }
}
