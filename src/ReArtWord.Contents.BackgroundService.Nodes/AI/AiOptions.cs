using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>公式APIの接続先とテスト時だけのループバック接続先を固定します。</summary>
/// <param name="LoopbackFixtureBaseUri">明示されたローカル接続線試験用の構成専用URIです。本番ではnullにします。</param>
public sealed record AiOptions(Uri? LoopbackFixtureBaseUri = null)
{
    /// <summary>公式サービス提供元のbase URI、または明示ループバック試験用の構成を取得します。</summary>
    /// <returns>任意外部URLへ設定で変更されない接続先です。</returns>
    internal Uri BaseUri(bool wan)
    {
        if (LoopbackFixtureBaseUri is not null)
        {
            bool valid = LoopbackFixtureBaseUri.IsAbsoluteUri && LoopbackFixtureBaseUri.IsLoopback && string.Equals(LoopbackFixtureBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && string.IsNullOrEmpty(LoopbackFixtureBaseUri.UserInfo);
            if (!valid)
            {
                throw new ArgumentException("fixture接続先はHTTP loopbackだけを指定できます。");
            }
            return LoopbackFixtureBaseUri;
        }
        if (wan)
        {
            return AiServiceConnection.Endpoint("ai.video.generate")!;
        }
        return AiServiceConnection.Endpoint("ai.text.generate")!;
    }

    /// <summary>Alibabaの明示リージョンを公式APIへ対応させます。</summary>
    /// <returns>APIキーと音声IDの所属先に一致させる接続先です。</returns>
    internal Uri AlibabaBaseUri(string region)
    {
        if (LoopbackFixtureBaseUri is not null)
        {
            return BaseUri(true);
        }
        return AiServiceConnection.Endpoint("ai.audio.qwen.generate", region) ?? throw new ArgumentOutOfRangeException(nameof(region));
    }

    /// <summary>ElevenLabs音声生成の公式接続先を取得します。</summary>
    /// <returns>公式APIまたは明示ループバック試験用の構成です。</returns>
    internal Uri ElevenLabsBaseUri()
    {
        if (LoopbackFixtureBaseUri is not null)
        {
            return BaseUri(false);
        }
        return AiServiceConnection.Endpoint("ai.audio.elevenlabs.generate")!;
    }
}
