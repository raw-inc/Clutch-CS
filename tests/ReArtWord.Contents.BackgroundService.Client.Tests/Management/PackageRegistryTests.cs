using System.Net;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Tests.Client;
using ReArtWord.Contents.BackgroundService.ClientHost.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>公式APIの応答境界・索引キャッシュ・固定版の選択を検証します。</summary>
public sealed class PackageRegistryTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("contents-package-search-").FullName;

    /// <summary>NuGetの接続先をindexから読み、検索語とページ範囲を伝えます。</summary>
    /// <returns>有限件の検索結果と導入候補の確認です。</returns>
    [Fact]
    public async Task NugetSearchUsesServiceIndexAndExactVersionsAsync()
    {
        using ClientHttpHandler handler = new((request, _) =>
        {
            string json = request.RequestUri!.AbsolutePath switch
            {
                "/v3/index.json" => """{"resources":[{"@type":"SearchQueryService/3.5.0","@id":"https://search.nuget.org/query"},{"@type":"PackageBaseAddress/3.0.0","@id":"https://api.nuget.org/flat/"}]}""",
                "/query" => """{"totalHits":21,"data":[{"id":"Example.Package","version":"1.2.0","description":"Description <b>text</b>","totalDownloads":123}]}""",
                "/flat/example.package/index.json" => """{"versions":["1.0.0","1.2.0","2.0.0-preview"]}""",
                _ => throw new InvalidOperationException("Unexpected registry route."),
            };
            return Task.FromResult(Reply(json));
        });
        using HttpClient http = new(handler);
        using PackageRegistryClient registry = new(directory, http);
        ScriptPackageSearchResult found = await registry.SearchAsync(new(EScriptPackageKind.Nuget, "Example")).ConfigureAwait(true);
        Assert.Null(found.Problem);
        Assert.True(found.HasMore);
        Assert.Equal(123, Assert.Single(found.Items).Downloads);
        ScriptPackageDetails details = await registry.DetailsAsync(new(EScriptPackageKind.Nuget, "Example.Package")).ConfigureAwait(true);
        Assert.Equal("1.2.0", details.LatestVersion);
        Assert.Equal("1.2.0", details.Versions[0]);
        Assert.Contains("2.0.0-preview", details.Versions);
        Assert.Equal("https://www.nuget.org/packages/example.package", details.RegistryUrl!.AbsoluteUri);
    }

    /// <summary>npmのscopeを一つの配布名として扱い、latestを固定版へ解決します。</summary>
    /// <returns>タグをインストール要求へ渡さない詳細です。</returns>
    [Fact]
    public async Task ScopedNpmDetailsReturnPinnedLatestAndDescriptionAsync()
    {
        using ClientHttpHandler handler = new((request, _) =>
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            string json = path switch
            {
                "/@scope/example" => """{"name":"@scope/example","dist-tags":{"latest":"2.0.0"},"versions":{"1.0.0":{},"2.0.0":{}}}""",
                "/@scope/example/2.0.0" => """{"description":"Scoped package"}""",
                _ => throw new InvalidOperationException("Unexpected registry route."),
            };
            return Task.FromResult(Reply(json));
        });
        using HttpClient http = new(handler);
        using PackageRegistryClient registry = new(directory, http);
        ScriptPackageDetails details = await registry.DetailsAsync(new(EScriptPackageKind.Npm, "@scope/example")).ConfigureAwait(true);
        Assert.Null(details.Problem);
        Assert.Equal("2.0.0", details.LatestVersion);
        Assert.Equal("Scoped package", details.Description);
        Assert.Equal(2, details.Versions.Count);
    }

    /// <summary>巨大なPyPI索引の途中でトークンが分割されても、名前だけを原子的に保存します。</summary>
    /// <returns>正規化・順位・ディスク再使用の確認です。</returns>
    [Fact]
    public async Task PypiNamesAreStreamedRankedAndCachedAcrossInstancesAsync()
    {
        string json = """{"meta":{"name":"ignored"},"projects":[{"name":"another-foo-bar"},{"name":"Foo_Bar"},{"name":"foo-bar-tools"},{"name":"unrelated"}]}""";
        int reads = 0;
        using ClientHttpHandler handler = new((_, _) =>
        {
            reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(json))) });
        });
        using HttpClient http = new(handler);
        using (PackageRegistryClient registry = new(directory, http))
        {
            ScriptPackageSearchResult result = await registry.SearchAsync(new(EScriptPackageKind.Pip, "FOO.bar")).ConfigureAwait(true);
            Assert.Null(result.Problem);
            Assert.Equal(["foo-bar", "foo-bar-tools", "another-foo-bar"], result.Items.Select(item => item.Id));
        }
        using HttpClient nextHttp = new(handler, disposeHandler: false);
        using PackageRegistryClient next = new(directory, nextHttp);
        Assert.Equal(3, (await next.SearchAsync(new(EScriptPackageKind.Pip, "foo-bar")).ConfigureAwait(true)).Items.Count);
        Assert.Equal(1, reads);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    /// <summary>期限切れ索引の更新が失敗しても、取得済みの名前を空で上書きしません。</summary>
    /// <returns>キャッシュ利用の明示と前の索引の保持です。</returns>
    [Fact]
    public async Task FailedIndexRefreshKeepsLastCompleteCacheAsync()
    {
        string cache = Path.Combine(directory, "pypi-projects.txt");
        await File.WriteAllTextAsync(cache, "example-package\n").ConfigureAwait(true);
        File.SetLastWriteTimeUtc(cache, DateTimeOffset.UtcNow.AddDays(-2).UtcDateTime);
        using ClientHttpHandler handler = new((_, _) => throw new HttpRequestException("Offline"));
        using HttpClient http = new(handler);
        using PackageRegistryClient registry = new(directory, http);
        ScriptPackageSearchResult result = await registry.SearchAsync(new(EScriptPackageKind.Pip, "example")).ConfigureAwait(true);
        Assert.Equal("example-package", Assert.Single(result.Items).Id);
        Assert.Equal("script.packages.pypiCachedSearch", result.Notice);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    /// <summary>配布元障害を検索0件と混同せず、不正名を通信へ渡しません。</summary>
    /// <returns>取得失敗と入力検証の境界です。</returns>
    [Fact]
    public async Task NetworkFailureAndInvalidPackageAreNotSuccessfulEmptyResultsAsync()
    {
        using ClientHttpHandler handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using HttpClient http = new(handler);
        using PackageRegistryClient registry = new(directory, http);
        ScriptPackageSearchResult failed = await registry.SearchAsync(new(EScriptPackageKind.Npm, "example")).ConfigureAwait(true);
        Assert.NotNull(failed.Problem);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.DetailsAsync(new(EScriptPackageKind.Npm, "../../outside"))).ConfigureAwait(true);
    }

    /// <summary>読取り取消では不完全な索引を公開しません。</summary>
    /// <returns>要求元へ伝わる取消と一時ファイルの解放です。</returns>
    [Fact]
    public async Task CancelledIndexDoesNotPublishPartialCacheAsync()
    {
        using ClientHttpHandler handler = new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Reply("{}");
        });
        using HttpClient http = new(handler);
        using PackageRegistryClient registry = new(directory, http);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.SearchAsync(new(EScriptPackageKind.Pip, "example"), cancel.Token)).ConfigureAwait(true);
        Assert.Empty(Directory.GetFiles(directory));
    }

    /// <summary>HTTP所有境界へUTF-8の公開メタデータを返します。</summary>
    /// <returns>呼出し側が破棄するJSON応答です。</returns>
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>この試験で作った一意の一時領域だけを削除します。</summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>UTF-8とプロパティ名の途中で読取りを分割する通信境界です。</summary>
    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        /// <summary>索引パーサーへ7バイトずつ渡します。</summary>
        /// <returns>今回の部分読取りです。</returns>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
    }
}
