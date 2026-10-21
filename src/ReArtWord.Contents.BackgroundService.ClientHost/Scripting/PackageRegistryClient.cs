using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Scripting;

/// <summary>公開配布元の検索と版一覧を取得し、導入処理から分離します。</summary>
internal sealed class PackageRegistryClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly SocketsHttpHandler? ownedHandler;
    private readonly PypiProjectIndex python;
    private NugetEndpoints? nuget;

    /// <summary>管理画面ホストで索引を共有し、Peerごとの重複取得を避けます。</summary>
    internal PackageRegistryClient(string cacheDirectory, HttpClient? client = null)
    {
        ownsClient = client is null;
        if (client is null)
        {
            ownedHandler = new() { AutomaticDecompression = DecompressionMethods.All };
            http = new(ownedHandler, disposeHandler: false);
        }
        else
        {
            http = client;
        }
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.MaxResponseContentBufferSize = 33554432;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Contents-Package-Browser/1.0");
        python = new(http, cacheDirectory);
    }

    /// <summary>一回の検索に有限のページ範囲を適用します。</summary>
    /// <returns>空の結果と配布元エラーを区別した検索結果です。</returns>
    internal async Task<ScriptPackageSearchResult> SearchAsync(ScriptPackageSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (!Enum.IsDefined(request.Kind) || 100 < request.Query.Length || request.Offset is < 0 or > 1000)
        {
            throw new ArgumentException("script.packages.searchInvalid", nameof(request));
        }
        try
        {
            string query = request.Query.Trim();
            if (request.Kind == EScriptPackageKind.Pip)
            {
                return await python.SearchAsync(query, request.Offset, cancellationToken).ConfigureAwait(false);
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            if (request.Kind == EScriptPackageKind.Nuget)
            {
                NugetEndpoints resources = await NugetResourcesAsync(timeout.Token).ConfigureAwait(false);
                Uri endpoint = new(resources.Search.AbsoluteUri + "?q=" + Uri.EscapeDataString(query) + "&skip=" + request.Offset + "&take=20&prerelease=false&semVerLevel=2.0.0");
                using JsonDocument data = await ReadAsync(endpoint, cancellationToken: timeout.Token).ConfigureAwait(false);
                ScriptPackageSearchItem[] items = Items(data.RootElement, "data").Where(item => ScriptPackageNames.Valid(Text(item, "id"), request.Kind)).Take(20)
                    .Select(item => new ScriptPackageSearchItem(Text(item, "id"), Text(item, "version"), Text(item, "description", 600), Number(item, "totalDownloads"))).ToArray();
                return new(items, request.Offset + items.Length < (Number(data.RootElement, "totalHits") ?? 0));
            }
            using JsonDocument npm = await ReadAsync(new("https://registry.npmjs.org/-/v1/search?text=" + Uri.EscapeDataString(query) + "&size=20&from=" + request.Offset), cancellationToken: timeout.Token).ConfigureAwait(false);
            ScriptPackageSearchItem[] packages = Items(npm.RootElement, "objects").Select(item => item.GetProperty("package"))
                .Where(item => ScriptPackageNames.Valid(Text(item, "name"), request.Kind)).Take(20)
                .Select(item => new ScriptPackageSearchItem(Text(item, "name"), Text(item, "version"), Text(item, "description", 600))).ToArray();
            return new(packages, request.Offset + packages.Length < (Number(npm.RootElement, "total") ?? 0));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Kind == EScriptPackageKind.Nuget)
            {
                nuget = null;
            }
            return new([], false, Problem: "script.packages.searchUnavailable");
        }
    }

    /// <summary>導入する固定版は公開元から選び、検索語を導入要求へ流用しません。</summary>
    /// <returns>最大500件の版と配布元の説明です。</returns>
    internal async Task<ScriptPackageDetails> DetailsAsync(ScriptPackageDetailsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ScriptPackageNames.Valid(request.Id, request.Kind))
        {
            throw new ArgumentException("script.packages.searchInvalid", nameof(request));
        }
        string id = ScriptPackageNames.Normalize(request.Id, request.Kind), encoded = Uri.EscapeDataString(id);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            if (request.Kind == EScriptPackageKind.Nuget)
            {
                NugetEndpoints resources = await NugetResourcesAsync(timeout.Token).ConfigureAwait(false);
                using JsonDocument index = await ReadAsync(new(resources.Packages, encoded + "/index.json"), cancellationToken: timeout.Token).ConfigureAwait(false);
                string[] versions = Items(index.RootElement, "versions").Select(item => item.GetString()!).Where(version => !string.IsNullOrWhiteSpace(version)).Reverse().ToArray();
                string latest = versions.FirstOrDefault(version => !version.Contains('-', StringComparison.Ordinal)) ?? versions.FirstOrDefault() ?? string.Empty;
                using JsonDocument metadata = await ReadAsync(new(resources.Search.AbsoluteUri + "?q=" + Uri.EscapeDataString("packageid:" + id) + "&take=20&semVerLevel=2.0.0"), cancellationToken: timeout.Token).ConfigureAwait(false);
                JsonElement entry = Items(metadata.RootElement, "data").FirstOrDefault(item => string.Equals(Text(item, "id"), id, StringComparison.OrdinalIgnoreCase));
                return Detail(id, Text(entry, "description", 4000), latest, versions, "https://www.nuget.org/packages/" + encoded);
            }
            if (request.Kind == EScriptPackageKind.Pip)
            {
                using JsonDocument index = await ReadAsync(new("https://pypi.org/simple/" + encoded + "/"), "application/vnd.pypi.simple.v1+json", timeout.Token).ConfigureAwait(false);
                using JsonDocument metadata = await ReadAsync(new("https://pypi.org/pypi/" + encoded + "/json"), cancellationToken: timeout.Token).ConfigureAwait(false);
                JsonElement info = metadata.RootElement.GetProperty("info");
                return Detail(id, Text(info, "summary", 4000), Text(info, "version"),
                    Items(index.RootElement, "versions").Select(item => item.GetString()!).Reverse(), "https://pypi.org/project/" + encoded + "/");
            }
            using JsonDocument npm = await ReadAsync(new("https://registry.npmjs.org/" + encoded), "application/vnd.npm.install-v1+json", timeout.Token).ConfigureAwait(false);
            JsonElement root = npm.RootElement;
            string current = Text(root.GetProperty("dist-tags"), "latest");
            JsonElement versionInfo = root.GetProperty("versions");
            using JsonDocument selected = await ReadAsync(new("https://registry.npmjs.org/" + encoded + "/" + Uri.EscapeDataString(current)), cancellationToken: timeout.Token).ConfigureAwait(false);
            string description = Text(selected.RootElement, "description", 4000);
            return Detail(id, description, current, versionInfo.EnumerateObject().Select(version => version.Name).Reverse(), "https://www.npmjs.com/package/" + id);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Kind == EScriptPackageKind.Nuget)
            {
                nuget = null;
            }
            return new(id, string.Empty, string.Empty, [], null, Problem: "script.packages.detailsUnavailable");
        }
    }

    /// <summary>NuGetの接続先を公式service indexから取得し、公開HTTPSに限定します。</summary>
    /// <returns>検索と版一覧に使用する接続先です。</returns>
    private async Task<NugetEndpoints> NugetResourcesAsync(CancellationToken cancellationToken = default)
    {
        if (nuget is { } cached)
        {
            return cached;
        }
        using JsonDocument document = await ReadAsync(new("https://api.nuget.org/v3/index.json"), cancellationToken: cancellationToken).ConfigureAwait(false);
        Dictionary<string, Uri> resources = new(StringComparer.Ordinal);
        string search = "SearchQueryService", packages = "PackageBaseAddress", suffix = ".nuget.org";
        foreach (JsonElement resource in Items(document.RootElement, "resources"))
        {
            string type = Text(resource, "@type");
            Uri uri = new(Text(resource, "@id"));
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || !uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (type.StartsWith(search, StringComparison.Ordinal))
            {
                resources.TryAdd("search", uri);
            }
            if (type.StartsWith(packages, StringComparison.Ordinal))
            {
                resources.TryAdd("packages", uri);
            }
        }
        if (resources.Count != 2)
        {
            throw new JsonException("Package source resources are missing.");
        }
        nuget = new(resources["search"], resources["packages"]);
        return nuget;
    }

    /// <summary>圧縮展開後の応答上限をHttpClientで適用し、JSONだけを読みます。</summary>
    /// <returns>呼出し元が破棄する有界JSONです。</returns>
    private async Task<JsonDocument> ReadAsync(Uri uri, string accept = "application/json", CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd(accept);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 64 });
    }

    /// <summary>最新候補を先頭へ置き、巨大な版一覧を有限にします。</summary>
    /// <returns>公開版の選択肢です。</returns>
    private static ScriptPackageDetails Detail(string id, string description, string latest, IEnumerable<string> versions, string url)
    {
        string[] limited = versions.Prepend(latest).Where(version => !string.IsNullOrWhiteSpace(version) && version.Length <= 128).Distinct(StringComparer.Ordinal).Take(501).ToArray();
        return new(id, description, latest, limited.Take(500).ToArray(), new(url), 500 < limited.Length);
    }

    /// <summary>配布元の省略可能な配列を安全に列挙します。</summary>
    /// <returns>存在する配列要素です。</returns>
    private static JsonElement.ArrayEnumerator Items(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray();
        }
        return default;
    }

    /// <summary>説明文をHTMLとして解釈せず、有限長の文字列にします。</summary>
    /// <returns>存在する文字列か空文字です。</returns>
    private static string Text(JsonElement value, string name, int maximum = 214)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            string result = text.GetString()!;
            return result[..Math.Min(result.Length, maximum)];
        }
        return string.Empty;
    }

    /// <summary>未提供の統計を0件として表示しません。</summary>
    /// <returns>取得できた件数かnullです。</returns>
    private static long? Number(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out JsonElement number) && number.TryGetInt64(out long count))
        {
            return count;
        }
        return null;
    }

    /// <summary>所有する通信と索引の排他を解放します。</summary>
    public void Dispose()
    {
        python.Dispose();
        if (ownsClient)
        {
            http.Dispose();
        }
        ownedHandler?.Dispose();
    }

    /// <summary>同時検索へ原子的に公開するNuGet接続先です。</summary>
    private sealed record NugetEndpoints(Uri Search, Uri Packages);
}
