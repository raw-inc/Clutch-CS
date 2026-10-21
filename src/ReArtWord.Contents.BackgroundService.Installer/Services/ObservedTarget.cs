using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>観測経路を含む対象です。ローカル停止配置はPeerを持ちません。</summary>
internal sealed record ObservedTarget(string ClusterId, string PeerId, ClientNetworkBinding Binding, PeerInfo? Peer, InstallationRecord? Installation = null)
{
    /// <summary>対象確認画面の表示です。</summary>
    public string Label => ClusterId + " / " + PeerId + " · " + Endpoint + " · " + (Peer?.Status.ToString() ?? "Offline") + " · " + Installation?.Settings.Root;

    /// <summary>別portに同名のクラスタ・登録名がある場合も区別する接続先です。</summary>
    private string Endpoint => (Peer?.Address ?? Binding.BindAddress) + ":" + Binding.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>選択言語で名前と利用状態を表示します。</summary>
    /// <returns>対象を見分ける表示名です。</returns>
    public string DisplayName(bool japanese)
    {
        string status = Peer?.Status switch
        {
            EPeerStatus.Ready => "Available",
            null => "Stopped or unreachable",
            _ => "Needs attention",
        };
        if (japanese)
        {
            status = Peer?.Status switch
            {
                EPeerStatus.Ready => "使用可能",
                null => "停止中・接続できません",
                _ => "確認が必要",
            };
        }
        return ClusterId + " / " + PeerId + " · " + Endpoint + " · " + status;
    }
}
