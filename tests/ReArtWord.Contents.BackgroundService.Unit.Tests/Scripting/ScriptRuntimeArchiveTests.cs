using System.IO.Compression;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>配布ZIPの不正パスとハッシュ不一致を、実ファイルとストリームで検証します。</summary>
public sealed class ScriptRuntimeArchiveTests
{
    /// <summary>展開先の外へ書く名前とシンボリックリンクを拒否します。</summary>
    /// <returns>展開が実行ファイルの公開前に止まる確認です。</returns>
    [Theory]
    [InlineData("../escaped.txt", false)]
    [InlineData("/absolute.txt", false)]
    [InlineData("C:/absolute.txt", false)]
    [InlineData("safe/../escaped.txt", false)]
    [InlineData("link", true)]
    public async Task UnsafeEntriesNeverEscapeStaging(string name, bool link)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-runtime-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "runtime.zip"), staged = Path.Combine(root, "stage");
            using (ZipArchive zip = await ZipFile.OpenAsync(archive, ZipArchiveMode.Create).ConfigureAwait(true))
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                if (link)
                {
                    entry.ExternalAttributes = 0xa000 << 16;
                }
                using Stream output = await entry.OpenAsync().ConfigureAwait(true);
                await output.WriteAsync(Encoding.UTF8.GetBytes("outside")).ConfigureAwait(true);
            }
            ScriptRuntimeRelease release = new("python", "3.15.0", "win-x64", false, new("https://www.python.org/unused.zip"), new string('a', 64));
            InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => ScriptRuntimeArchive.ExtractAsync(archive, staged, release)).ConfigureAwait(true);
            Assert.Equal("script.runtime.archivePath", failure.Message);
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>受信できても公式ハッシュが一致しなければ採用しません。</summary>
    /// <returns>ダウンロード成功と検証成功を区別する確認です。</returns>
    [Fact]
    public async Task WrongChecksumRejectsDownloadedContent()
    {
        string path = Path.Combine(Path.GetTempPath(), "contents-runtime-download-" + Guid.NewGuid().ToString("N"));
        using RuntimeBytesHandler handler = new();
        using HttpClient client = new(handler);
        try
        {
            ScriptRuntimeRelease release = new("python", "3.15.0", "win-x64", false, new("https://www.python.org/unused.zip"), new string('a', 64));
            InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => ScriptRuntimeArchive.DownloadAsync(client, release, path, (_, _) =>
            {
                // 進捗の頻度はここでは検証対象としません。
            })).ConfigureAwait(true);
            Assert.Equal("script.runtime.checksumMismatch", failure.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>外部ネットワークへ接続せず、誤った配布物を返すHTTP境界です。</summary>
internal sealed class RuntimeBytesHandler : HttpMessageHandler
{
    /// <summary>ハッシュ検証のため、正常応答で異なる本文を返します。</summary>
    /// <returns>有限の試験本文です。</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("incorrect payload") });
}
