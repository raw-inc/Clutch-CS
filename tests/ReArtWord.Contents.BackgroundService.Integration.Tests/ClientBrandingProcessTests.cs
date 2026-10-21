using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実ClientHostでタブアイコンの既定値、独立指定、再起動による差替えを確認します。</summary>
public sealed class ClientBrandingProcessTests
{
    /// <summary>既存の設定ファイルがなくても、表示設定と同梱のSVG画像を配信します。</summary>
    /// <returns>実HTTPで取得できる既定アイコンです。</returns>
    [Fact]
    public async Task DefaultBrandingProvidesTheBundledVectorIconAsync()
    {
        ClientHostProcess client = new(DirectoryPath());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = client.ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/branding").ConfigureAwait(true);
        ClientBranding? branding = await response.Content.ReadFromJsonAsync<ClientBranding>(ContractJson.Options).ConfigureAwait(true);
        Assert.NotNull(branding);
        Assert.Equal("Clutch-CS", branding.ContentsName);
        Assert.Equal("WebUI", branding.Subtitle);
        Assert.Null(branding.Favicon);
        using HttpResponseMessage icon = await client.SendAsync(HttpMethod.Get, "favicon.svg").ConfigureAwait(true);
        icon.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType!.MediaType);
        XDocument svg = XDocument.Parse(await icon.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal(XName.Get("svg", "http://www.w3.org/2000/svg"), svg.Root!.Name);
        Assert.Equal("0 0 32 32", svg.Root.Attribute("viewBox")!.Value);
    }

    /// <summary>faviconだけを指定し、開始後の画像差替えは再起動まで持ち込まず、その後は別URLで公開します。</summary>
    /// <returns>ICOの配信、ヘッダーからの独立、更新後の新しい取得先です。</returns>
    [Fact]
    public async Task DedicatedIconOverridesHeaderAndChangesIdentityAfterRestartAsync()
    {
        string directory = DirectoryPath();
        Directory.CreateDirectory(directory);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aS1sAAAAASUVORK5CYII=");
        byte[] ico = [0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 32, 0, (byte)png.Length, 0, 0, 0, 22, 0, 0, 0, .. png];
        await File.WriteAllBytesAsync(Path.Combine(directory, "header.png"), png).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(directory, "tab.ICO"), ico).ConfigureAwait(true);
        ClientBranding settings = new(ContentsName: "Example installation", HeaderLogo: "header.png", Favicon: "tab.ICO", Subtitle: "Control console");
        await File.WriteAllTextAsync(Path.Combine(directory, "branding.json"), JsonSerializer.Serialize(settings, ContractJson.Options)).ConfigureAwait(true);
        ClientHostProcess client = new(directory);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = client.ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/branding").ConfigureAwait(true);
        ClientBranding branding = (await response.Content.ReadFromJsonAsync<ClientBranding>(ContractJson.Options).ConfigureAwait(true))!;
        Assert.Equal("Example installation", branding.ContentsName);
        Assert.Equal("Control console", branding.Subtitle);
        Assert.StartsWith("/client/branding/favicon?v=", branding.Favicon, StringComparison.Ordinal);
        Assert.NotEqual(branding.HeaderLogo, branding.Favicon);
        using HttpResponseMessage icon = await client.SendAsync(HttpMethod.Get, branding.Favicon!).ConfigureAwait(true);
        Assert.Equal("image/x-icon", icon.Content.Headers.ContentType!.MediaType);
        Assert.True(icon.Headers.CacheControl!.NoStore);
        Assert.Equal(ico, await icon.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        byte[] changed = [.. ico, 0];
        await File.WriteAllBytesAsync(Path.Combine(directory, "tab.ICO"), changed).ConfigureAwait(true);
        using HttpResponseMessage retained = await client.SendAsync(HttpMethod.Get, branding.Favicon!).ConfigureAwait(true);
        Assert.Equal(ico, await retained.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        await client.StopAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage updated = await client.SendAsync(HttpMethod.Get, "client/branding").ConfigureAwait(true);
        ClientBranding next = (await updated.Content.ReadFromJsonAsync<ClientBranding>(ContractJson.Options).ConfigureAwait(true))!;
        Assert.NotEqual(branding.Favicon, next.Favicon);
        Assert.Equal(branding.HeaderLogo, next.HeaderLogo);
        Assert.Equal(branding.Subtitle, next.Subtitle);
        using HttpResponseMessage replacement = await client.SendAsync(HttpMethod.Get, next.Favicon!).ConfigureAwait(true);
        Assert.Equal(changed, await replacement.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
    }

    /// <summary>保存先を本番Clientから分離します。</summary>
    /// <returns>今回の実プロセス専用の保存先です。</returns>
    private static string DirectoryPath() => Path.GetFullPath("../../../../../.work/integration/branding-" + Guid.NewGuid().ToString("N"), AppContext.BaseDirectory);
}
