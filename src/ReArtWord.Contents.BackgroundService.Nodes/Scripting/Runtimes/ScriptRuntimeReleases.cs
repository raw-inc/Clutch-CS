using System.Runtime.InteropServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

/// <summary>公式の安定版配布一覧から、このOSとCPUで導入できる完全な実行環境だけを公開します。</summary>
internal sealed class ScriptRuntimeReleases(HttpClient client) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<ScriptRuntimeRelease> Releases)> cache = new(StringComparer.Ordinal);

    /// <summary>バージョン選択は一時間共有し、インストール要求に任意のURLを含めません。</summary>
    /// <returns>対象プラットフォームの検証可能な公式配布物です。</returns>
    internal async Task<IReadOnlyList<ScriptRuntimeRelease>> ReadAsync(string language, CancellationToken cancellationToken = default)
    {
        language = ScriptRuntimePaths.Language(language);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new InvalidOperationException("script.runtime.installPlatformUnsupported");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(language, out (DateTimeOffset At, IReadOnlyList<ScriptRuntimeRelease> Releases) previous) && DateTimeOffset.UtcNow - previous.At < TimeSpan.FromHours(1))
            {
                return previous.Releases;
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            IReadOnlyList<ScriptRuntimeRelease> releases;
            string python = "python";
            if (string.Equals(language, python, StringComparison.Ordinal))
            {
                releases = await PythonAsync(timeout.Token).ConfigureAwait(false);
            }
            else
            {
                byte[] json = await ReadBytesAsync(new("https://nodejs.org/download/release/index.json"), 4194304, timeout.Token).ConfigureAwait(false);
                releases = Node(json, Platform());
            }
            cache[language] = (DateTimeOffset.UtcNow, releases);
            return releases;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Node.jsのハッシュ一覧から選択済みのZIP一つの値を取り出します。</summary>
    /// <returns>SHA-256を含む配布物です。</returns>
    internal async Task<ScriptRuntimeRelease> WithHashAsync(ScriptRuntimeRelease release, CancellationToken cancellationToken = default)
    {
        if (release.Sha256 is not null)
        {
            return release;
        }
        Uri checksum = new(release.Url, "SHASUMS256.txt");
        byte[] bytes = await ReadBytesAsync(checksum, 1048576, cancellationToken).ConfigureAwait(false);
        string file = Path.GetFileName(release.Url.AbsolutePath);
        string? hash = null;
        foreach (string line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1], file, StringComparison.Ordinal) && ValidHash(parts[0]))
            {
                hash = parts[0];
                break;
            }
        }
        return release with { Sha256 = hash ?? throw new InvalidDataException("script.runtime.checksumMissing") };
    }

    /// <summary>メタデータでも長さ上限を設け、無制限にバッファへ取り込みません。</summary>
    /// <returns>有限の配布情報です。</returns>
    private async Task<byte[]> ReadBytesAsync(Uri address, int maximum, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (maximum < response.Content.Headers.ContentLength)
        {
            throw new InvalidDataException("script.runtime.metadataLimit");
        }
        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] block = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (maximum < buffer.Length + count)
            {
                throw new InvalidDataException("script.runtime.metadataLimit");
            }
            await buffer.WriteAsync(block.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    /// <summary>完全版Pythonだけを採用し、pip非対応の埋込み版と試験版を除外します。</summary>
    /// <returns>最新パッチ順の候補です。</returns>
    private async Task<IReadOnlyList<ScriptRuntimeRelease>> PythonAsync(CancellationToken cancellationToken = default)
    {
        List<ScriptRuntimeRelease> releases = [];
        Uri index = new("https://www.python.org/ftp/python/index-windows.json");
        string platform = Platform();
        for (int page = 0; page < 4; page++)
        {
            byte[] json = await ReadBytesAsync(index, 4194304, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            releases.AddRange(Python(document.RootElement, platform));
            string nextName = "next";
            if (!document.RootElement.TryGetProperty(nextName, out JsonElement next) || next.ValueKind != JsonValueKind.String)
            {
                break;
            }
            Uri following = new(index, next.GetString());
            string host = "www.python.org", prefix = "/ftp/python/index-windows";
            if (!string.Equals(following.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || !string.Equals(following.Host, host, StringComparison.OrdinalIgnoreCase) || !following.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("script.runtime.metadataInvalid");
            }
            index = following;
        }
        return releases.DistinctBy(release => release.Version).OrderByDescending(release => Version.Parse(release.Version)).Take(80).ToArray();
    }

    /// <summary>プラットフォームは対象CoreのOSアーキテクチャから決めます。</summary>
    /// <returns>公式配布物の接尾辞です。</returns>
    private static string Platform() => RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "win-arm64", _ => "win-x64" };

    /// <summary>外部配布表のハッシュ文字列を検証します。</summary>
    /// <returns>32バイトの16進表記かです。</returns>
    internal static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>公式Python一覧の安定版フルZIPを型付き候補へ変換します。</summary>
    /// <returns>指定CPU用の候補です。</returns>
    internal static IReadOnlyList<ScriptRuntimeRelease> Python(JsonElement root, string platform)
    {
        List<ScriptRuntimeRelease> result = [];
        string prefix = "pythoncore-", executable = ".\\python.exe", host = "www.python.org", freeThreaded = "t-", executableKey = "executable";
        string suffix = platform switch { "win-arm64" => "-arm64.zip", _ => "-amd64.zip" };
        foreach (JsonElement item in root.GetProperty("versions").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string version = item.GetProperty("sort-version").GetString()!;
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || id.Contains(freeThreaded, StringComparison.Ordinal) || !Version.TryParse(version, out Version? parsed) || parsed < new Version(3, 10)
                || !string.Equals(item.GetProperty(executableKey).GetString(), executable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Uri url = new(item.GetProperty("url").GetString()!);
            string hash = item.GetProperty("hash").GetProperty("sha256").GetString()!;
            if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase) && url.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) && ValidHash(hash))
            {
                result.Add(new("python", version, platform, false, url, hash));
            }
        }
        return result;
    }

    /// <summary>Node.js24以降の安定版とCPU一致するZIPだけを選びます。</summary>
    /// <returns>LTSを先頭とするバージョン選択肢です。</returns>
    internal static IReadOnlyList<ScriptRuntimeRelease> Node(ReadOnlyMemory<byte> json, string platform)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<ScriptRuntimeRelease> result = [];
        string file = platform + "-zip", files = "files";
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string version = item.GetProperty("version").GetString()!.TrimStart('v');
            if (!Version.TryParse(version, out Version? parsed) || parsed.Major < 24 || !item.GetProperty(files).EnumerateArray().Any(value => string.Equals(value.GetString(), file, StringComparison.Ordinal)))
            {
                continue;
            }
            result.Add(new("javascript", version, platform, item.GetProperty("lts").ValueKind == JsonValueKind.String,
                new("https://nodejs.org/download/release/v" + version + "/node-v" + version + "-" + platform + ".zip"), null));
        }
        return result.OrderByDescending(release => release.LongTermSupport).ThenByDescending(release => Version.Parse(release.Version)).Take(80).ToArray();
    }

    /// <summary>呼出元が要求を終了した後、カタログ取得の待機資源を解放します。</summary>
    public void Dispose() => gate.Dispose();
}
