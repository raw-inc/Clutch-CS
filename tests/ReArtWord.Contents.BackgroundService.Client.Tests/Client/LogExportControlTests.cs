using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Pages.Logs;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実Razorの出力ボタンが現在の条件と標準ダウンロードを使うことを確認します。</summary>
public sealed class LogExportControlTests
{
    /// <summary>未検索のフォーム値も採用し、本文をブラウザーの管理APIで取得しません。</summary>
    /// <returns>選択範囲・受付・ダウンロード開始・完了表示です。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ButtonExportsCurrentFiltersWithoutBufferingZipAsync(bool allClusters)
    {
        LogExportRequest? received = null;
        Guid id = Guid.NewGuid();
        DownloadScript script = new();
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            object value;
            if (request.Method == HttpMethod.Post)
            {
                received = await request.Content!.ReadFromJsonAsync<LogExportRequest>(ContractJson.Options, token).ConfigureAwait(false);
                value = new LogExportStatus(id, "queued", 0, false, null, "logs.zip", DateTimeOffset.UtcNow.AddMinutes(2));
            }
            else
            {
                Assert.Equal("/client/log-exports/" + id, request.RequestUri!.AbsolutePath);
                Assert.Equal("/client/log-exports/" + id + "/download", script.Url);
                value = new LogExportStatus(id, "completed", 20, true, null, "logs.zip", DateTimeOffset.UtcNow.AddMinutes(15));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new("http://localhost") };
        ExportActivator activator = new();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new ManagementApi(http));
        services.AddSingleton<IJSRuntime>(script);
        services.AddSingleton<BrowserStorage>();
        services.AddSingleton(provider => new UiText(provider.GetRequiredService<BrowserStorage>()));
        services.AddSingleton<IComponentActivator>(activator);
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable rendererLifetime = renderer.ConfigureAwait(true);
        LogQuery query = new(Text: "latest", Levels: ["WARN"], WorkflowId: "wf", NodeId: "node", UncompressedOnly: false);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(LogExportControl.Query)] = (Func<LogQuery>)(() => query),
                [nameof(LogExportControl.AllClusters)] = allClusters,
                [nameof(LogExportControl.ClusterId)] = "cluster",
                [nameof(LogExportControl.PeerId)] = "peer",
            });
            Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent output = await renderer.RenderComponentAsync<LogExportControl>(parameters).ConfigureAwait(true);
            Assert.Contains("Download ZIP", output.ToHtmlString(), StringComparison.Ordinal);
            MethodInfo start = typeof(LogExportControl).GetMethod("StartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await ((Task)start.Invoke(activator.Component, null)!).ConfigureAwait(true);
            Assert.Contains("Check your browser downloads", output.ToHtmlString(), StringComparison.Ordinal);
        });
        Assert.Equal(JsonSerializer.Serialize(query, ContractJson.Options), JsonSerializer.Serialize(received!.Query, ContractJson.Options));
        Assert.Equal("UTC", received.TimeZoneId);
        if (allClusters)
        {
            Assert.Null(received.Source);
        }
        else
        {
            Assert.Equal(new("cluster", "peer"), received.Source);
        }
    }

    /// <summary>本文を受け取らず、リンク起動だけを観測します。</summary>
    private sealed class DownloadScript : IJSRuntime
    {
        internal string? Url
        {
            get; private set;
        }

        /// <summary>取消なしの呼出しも同じ確認へ渡します。</summary>
        /// <returns>ブラウザー専用処理の完了です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        /// <summary>Blob等の本文ではなく同一サイトの保存URLだけを受け付けます。</summary>
        /// <returns>リンクの起動確認です。</returns>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("contentsBrowser.downloadUrl", identifier);
            Url = Assert.IsType<string>(Assert.Single(args!));
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    /// <summary>実イベントを呼ぶため、生成されたコンポーネントを保持します。</summary>
    private sealed class ExportActivator : IComponentActivator
    {
        internal LogExportControl? Component
        {
            get; private set;
        }

        /// <summary>標準生成を維持し、対象だけを保持します。</summary>
        /// <returns>実Razorコンポーネントです。</returns>
        public IComponent CreateInstance(Type componentType)
        {
            IComponent component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is LogExportControl control)
            {
                Component = control;
            }
            return component;
        }
    }
}
