namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>HostManagerの変更種類です。</summary>
public enum EDeviceAction
{
    /// <summary>指定したNIC設定項目の変更。</summary>
    Network,

    /// <summary>管理OS用vNICの作成。</summary>
    VirtualNicCreate,

    /// <summary>確認済み管理OS用vNICの削除。</summary>
    VirtualNicDelete,

    /// <summary>管理OS用vNICのVLAN変更。</summary>
    VirtualNicVlan,

    /// <summary>hostsファイルの置換。</summary>
    Hosts,

    /// <summary>プロファイルの有効無効。</summary>
    FirewallProfile,

    /// <summary>ローカルルールの追加。</summary>
    FirewallRuleAdd,

    /// <summary>確認済みローカルルールの削除。</summary>
    FirewallRuleRemove,

    /// <summary>ローカルルールの有効無効。</summary>
    FirewallRuleEnabled,

    /// <summary>指定スコープの一つの環境変数を変更。</summary>
    Environment,

    /// <summary>Hyper-V仮想スイッチの作成。</summary>
    VirtualSwitchCreate,

    /// <summary>確認済みHyper-V仮想スイッチの削除。</summary>
    VirtualSwitchDelete,

    /// <summary>同一NICのIPv4/IPv6 bindingをまとめて変更。</summary>
    NetworkProtocols,

    /// <summary>固定したディスプレイの解像度・リフレッシュレート変更。</summary>
    Display,

    /// <summary>電源設定の部分変更。</summary>
    Power,

    /// <summary>Windows Updateの手動更新ポリシー設定。</summary>
    WindowsUpdate,

    /// <summary>次回ログオンの自動ログイン設定。</summary>
    Login,

    /// <summary>Wake-on-LANの送信対象NICを変更。</summary>
    WakeOnLan,
}
