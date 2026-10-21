using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>実npm CLIが取得するpackumentとtarballを、外部通信なしで提供します。</summary>
internal sealed class NpmRegistryFixture : IAsyncDisposable
{
    private readonly Dictionary<string, AiWireResponse> responses = new(StringComparer.Ordinal);
    private readonly AiWireFixture server;

    /// <summary>scoped ESMと推移依存のCommonJSを、それぞれ二つの版で公開します。</summary>
    internal NpmRegistryFixture()
    {
        server = new((request, _) => Task.FromResult(responses.GetValueOrDefault(Uri.UnescapeDataString(request.Path), AiWireResponse.Json("{\"error\":\"not_found\"}", 404))));
        Add("contents-test-dependency", "commonjs", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.0.0"] = "module.exports = () => 'first';",
            ["2.0.0"] = "module.exports = () => 'second';",
        }, dependency: false);
        Add("@contents/entry", "module", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.0.0"] = "import read from 'contents-test-dependency'; export { read };",
            ["2.0.0"] = "import read from 'contents-test-dependency'; export { read };",
        }, dependency: true);
    }

    /// <summary>試験用に固定するregistryのURLです。</summary>
    internal string Address => server.BaseUri.AbsoluteUri;

    /// <summary>すべてのHTTP応答が終了してからlistenerを解放します。</summary>
    /// <returns>試験配布元の終了です。</returns>
    public ValueTask DisposeAsync() => server.DisposeAsync();

    /// <summary>版ごとにintegrityを持つ通常のnpmメタデータを用意します。</summary>
    private void Add(string name, string moduleType, IReadOnlyDictionary<string, string> sources, bool dependency)
    {
        Dictionary<string, object> versions = new(StringComparer.Ordinal);
        foreach ((string version, string source) in sources)
        {
            Dictionary<string, string> dependencies = new(StringComparer.Ordinal);
            if (dependency)
            {
                dependencies["contents-test-dependency"] = version;
            }
            string path = "files/" + name.Replace('@', '_').Replace('/', '_') + "-" + version + ".tgz";
            object manifest = new { name, version, type = moduleType, main = "index.js", exports = new Dictionary<string, string>(StringComparer.Ordinal) { ["."] = "./index.js", ["./extra"] = "./extra.js" }, dependencies };
            byte[] tarball = Archive(JsonSerializer.Serialize(manifest), source);
            responses["/" + path] = new(200, "application/octet-stream", tarball);
            versions[version] = new { name, version, dependencies, dist = new { tarball = new Uri(server.BaseUri, path).AbsoluteUri, integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)) } };
        }
        responses["/" + name] = AiWireResponse.Json(JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["dist-tags"] = new { latest = "2.0.0" },
            ["versions"] = versions,
        }));
    }

    /// <summary>npmが標準展開するpackageディレクトリーを持つtgzです。</summary>
    /// <returns>配布する圧縮バイト列です。</returns>
    private static byte[] Archive(string manifest, string source)
    {
        using MemoryStream output = new();
        using (GZipStream compressed = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            using TarWriter archive = new(compressed, leaveOpen: true);
            Write(archive, "package/package.json", manifest);
            Write(archive, "package/index.js", source);
            Write(archive, "package/extra.js", "export default '!';");
        }
        return output.ToArray();
    }

    /// <summary>試験で指定した有限ファイルだけをアーカイブへ保存します。</summary>
    private static void Write(TarWriter archive, string path, string content)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        PaxTarEntry entry = new(TarEntryType.RegularFile, path) { DataStream = stream };
        archive.WriteEntry(entry);
    }
}
