namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>HostManagerの設定領域です。</summary>
public enum EDeviceArea
{
    /// <summary>NIC・IP・Hyper-Vの設定。</summary>
    Network,

    /// <summary>名前解決用hostsファイル。</summary>
    Hosts,

    /// <summary>Windows Firewall。</summary>
    Firewall,

    /// <summary>永続環境変数。</summary>
    Environment,

    /// <summary>接続ディスプレイの表示モード。</summary>
    Display,

    /// <summary>電源プランとPC共通の無操作停止設定。</summary>
    Power,

    /// <summary>Windows Updateの自動実行ポリシー。</summary>
    WindowsUpdate,

    /// <summary>Windowsの自動ログイン。</summary>
    Login,

    /// <summary>Wake-on-LANの送信対象NIC。</summary>
    WakeOnLan,
}
