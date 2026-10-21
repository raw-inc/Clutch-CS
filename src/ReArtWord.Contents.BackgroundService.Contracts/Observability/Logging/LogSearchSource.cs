namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>ログを取得したクラスタとPeerを、画面の接続先変更から独立して保持します。</summary>
/// <param name="ClusterId">取得元クラスタです。</param>
/// <param name="PeerId">保持ログを照会するPeerです。</param>
public readonly record struct LogSearchSource(string ClusterId, string PeerId)
{
    /// <summary>同名Peerが別クラスタにも存在する場合の表示名です。</summary>
    public string Label => ClusterId + " / " + PeerId;
}
