using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

/// <summary>Peer参加を伴わない管理クライアントの通信設定です。</summary>
/// <param name="ObserverId">クライアント自身の識別子。</param>
/// <param name="Binding">クライアント上で明示選択したNICと範囲。</param>
public sealed record ObserverOptions(string ObserverId, ClientNetworkBinding Binding)
{
    /// <summary>インストーラーでは、別ポートで動作するクラスターの発見通知も受信します。</summary>
    public bool IncludeDiscoveryAnnouncements
    {
        get; init;
    }

    /// <summary>一配送の応答待ち上限です。</summary>
    public TimeSpan RequestTimeout
    {
        get; init;
    } = TimeSpan.FromSeconds(10);

    /// <summary>実受信が途絶えた発見情報の保持時間です。</summary>
    public TimeSpan DiscoveryLifetime
    {
        get; init;
    } = TimeSpan.FromSeconds(15);

    /// <summary>管理要求・応答の上限です。</summary>
    public int MaximumMessageBytes
    {
        get; init;
    } = 4 * 1024 * 1024;
}
