using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Components.Files;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Browsing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>実Razorと管理HTTP境界で、ブラウザの接続先ではなく登録先のファイルを参照することを確認します。</summary>
public sealed class ApplicationFilePickerTests
{
    /// <summary>Windowsホストのパスをブラウザ実行OSのパスAPIで壊しません。</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("player", null)]
    [InlineData("C:\\player.exe", "C:\\")]
    [InlineData("C:\\Apps\\Player.exe", "C:\\Apps")]
    [InlineData("C:/Apps/Player.exe", "C:/Apps")]
    [InlineData("\\\\server\\share\\Player.exe", "\\\\server\\share")]
    [InlineData("/player", "/")]
    [InlineData("/opt/apps/player", "/opt/apps")]
    public void InitialFolderUsesTheRemotePathSyntax(string? path, string? expected)
        => Assert.Equal(expected, HostFilePicker.InitialDirectory(path));

    /// <summary>別ホストが全体選択されていても、指定所有者への読取り以外を送信しません。</summary>
    /// <returns>ファイル一覧の描画と所有者未確認時の非送信です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BrowseUsesExplicitApplicationOwnerAndNeverLaunchesAsync(bool ownerMissing, bool denied)
    {
        int requests = 0, picked = 0;
        string cluster = "application-cluster";
        int expectedRequests = 1;
        if (ownerMissing)
        {
            cluster = string.Empty;
            expectedRequests = 0;
        }
        using ClientHttpHandler handler = new(async (request, token) =>
        {
            requests++;
            Assert.Equal("/api/files/browse", request.RequestUri!.AbsolutePath);
            Assert.Equal("application-cluster", Assert.Single(request.Headers.GetValues("X-Contents-Cluster")));
            Assert.Equal("application-peer", Assert.Single(request.Headers.GetValues("X-Contents-Peer")));
            FileBrowseRequest? input = await request.Content!.ReadFromJsonAsync<FileBrowseRequest>(ContractJson.Options, token).ConfigureAwait(false);
            Assert.Equal("C:\\Apps", input!.Path);
            Assert.True(input.FromDrives);
            FileBrowseResult result = new("C:\\Apps", "C:\\", [new("Folder", "C:\\Apps\\Folder", true), new("Player.exe", "C:\\Apps\\Player.exe", false)]);
            if (denied)
            {
                result = result with { Entries = [], Problem = "files.denied" };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: ContractJson.Options) };
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost/") };
        ManagementApi api = new(http) { SelectedClusterId = "other-cluster", SelectedPeerId = "other-peer" };
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(api);
        services.AddSingleton<IJSRuntime>(new BrowserTestRuntime());
        services.AddSingleton(new UiText(new(new BrowserTestRuntime())));
        using ServiceProvider provider = services.BuildServiceProvider();
        HtmlRenderer renderer = new(provider, provider.GetRequiredService<ILoggerFactory>());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = renderer.ConfigureAwait(true);
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
            {
                [nameof(HostFilePicker.ClusterId)] = cluster,
                [nameof(HostFilePicker.PeerId)] = "application-peer", [nameof(HostFilePicker.Owner)] = "Application-host / application-cluster / application-peer",
                [nameof(HostFilePicker.InitialPath)] = "C:\\Apps\\Player.exe",
                [nameof(HostFilePicker.Picked)] = EventCallback.Factory.Create<string>(this, _ => picked++),
            };
            return (await renderer.RenderComponentAsync<HostFilePicker>(ParameterView.FromDictionary(parameters)).ConfigureAwait(true)).ToHtmlString();
        });
        Assert.Equal(expectedRequests, requests);
        Assert.Equal(0, picked);
        Assert.Contains("Application-host", html, StringComparison.Ordinal);
        Assert.Contains("Select application executable", html, StringComparison.Ordinal);
        if (ownerMissing || denied)
        {
            Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("file-picker-name", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Player.exe", html, StringComparison.Ordinal);
            Assert.Contains("file-picker-name", html, StringComparison.Ordinal);
        }
    }
}
