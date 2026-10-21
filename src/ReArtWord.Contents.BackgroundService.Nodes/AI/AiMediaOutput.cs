using ReArtWord.Contents.BackgroundService.Nodes.Media;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>有料生成前に必須のローカル検証能力を確認します。</summary>
internal static class AiMediaOutput
{
    /// <summary>生成後に検証不能とならないようprobe実行物を先に確認します。</summary>
    internal static void RequireProbe(MediaOptions options)
    {
        if (!Path.IsPathFullyQualified(options.FfprobePath) || !File.Exists(options.FfprobePath))
        {
            throw new FileNotFoundException("明示したffprobeがありません。");
        }
    }

    /// <summary>Alibabaの生成物URLを確認し、HTTP表記の音声URLはHTTPSで取得します。</summary>
    /// <returns>認証ヘッダーを付けずに取得する公式配布先です。</returns>
    internal static Uri AlibabaDownloadUri(AiOptions options, Uri uri, bool upgradeHttp = false)
    {
        if (options.LoopbackFixtureBaseUri is not null)
        {
            Uri fixture = options.BaseUri(true);
            if (uri.IsAbsoluteUri && uri.IsLoopback && string.Equals(uri.GetLeftPart(UriPartial.Authority), fixture.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            {
                return uri;
            }
            throw new InvalidDataException("生成物URLが指定fixtureのoriginと一致しません。");
        }
        bool secure = uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        bool upgrade = upgradeHttp && uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
        string providerSuffix = ".aliyuncs.com";
        if ((!secure && !upgrade) || !uri.Host.EndsWith(providerSuffix, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("生成物URLが許可されたAlibaba配布先ではありません。");
        }
        if (upgrade)
        {
            return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }
        return uri;
    }
}
