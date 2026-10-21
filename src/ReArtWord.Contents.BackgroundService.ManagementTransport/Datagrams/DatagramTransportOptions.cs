namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>Peer参加と独立したUDP配送の明示設定です。</summary>
/// <param name="SenderId">送信主体の識別子。監視処理もPeerとは別の値を持ちます。</param>
/// <param name="BindAddress">固定するlocal IPv4。</param>
/// <param name="BroadcastAddress">明示したブロードキャスト範囲。</param>
/// <param name="Port">共通受信ポート。</param>
/// <param name="ClusterId">受信するクラスタ。nullは監視処理の発見監視です。</param>
public sealed record DatagramTransportOptions(string SenderId, string BindAddress, string BroadcastAddress, int Port, string? ClusterId = null)
{
    /// <summary>同じ配送の応答待ち上限です。</summary>
    public TimeSpan RequestTimeout
    {
        get; init;
    } = TimeSpan.FromSeconds(10);

    /// <summary>一配送の再組立上限です。</summary>
    public int MaximumMessageBytes
    {
        get; init;
    } = 4 * 1024 * 1024;
}
