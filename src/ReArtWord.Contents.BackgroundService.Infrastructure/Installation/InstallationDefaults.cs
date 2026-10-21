namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>配置管理の有限待機と入力上限を一か所へ集約します。</summary>
public static class InstallationDefaults
{
    /// <summary>Windows のサービス導入で共用する配置先です。</summary>
    public static string WindowsServiceRoot => @"C:\Clutch-CS";

    /// <summary>既存Coreと共通の管理通信portです。</summary>
    public static int ManagementPort => ManagementTransport.Datagrams.DiscoveryProtocol.Port;

    /// <summary>新規クラスタの既定優先度です。</summary>
    public static int InitialPriority => 1000;

    /// <summary>一台だけの参加先で希望する順位差です。</summary>
    public static int PreferredPriorityGap => 10;

    /// <summary>名前をファイル名として使用する場合の上限です。</summary>
    public static int MaximumNameLength => 100;

    /// <summary>表示画像の最大サイズです。</summary>
    public static long MaximumImageBytes => Contracts.Management.ClientBranding.MaximumImageBytes;

    /// <summary>一つのNICの受動観測時間です。</summary>
    public static TimeSpan DiscoveryWindow => TimeSpan.FromSeconds(3);

    /// <summary>正常終了とOS commandの待機時間です。</summary>
    public static TimeSpan StopTimeout => TimeSpan.FromSeconds(30);

    /// <summary>起動後の通常参加確認時間です。</summary>
    public static TimeSpan ReadyTimeout => TimeSpan.FromSeconds(60);

    /// <summary>結果照会の一往復の待機時間です。</summary>
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(2);

    /// <summary>結果を再確認する間隔です。</summary>
    public static TimeSpan PollInterval => TimeSpan.FromMilliseconds(250);

    /// <summary>独立helperの最終結果を遠隔へ返す時間です。</summary>
    public static TimeSpan ResultRetention => TimeSpan.FromSeconds(90);

    /// <summary>遠隔保守全体を待機する上限です。</summary>
    public static TimeSpan RemoteTimeout => TimeSpan.FromMinutes(5);

    /// <summary>クライアントの既定port範囲の開始値です。</summary>
    public static int ClientPortStart => 20000;

    /// <summary>配置IDから選ぶクライアントport範囲の幅です。</summary>
    public static int ClientPortCount => 20000;

    /// <summary>クライアント起動後にブラウザーを開くまでの猶予です。</summary>
    public static TimeSpan ClientOpenDelay => TimeSpan.FromSeconds(2);

    /// <summary>既存Windows導入スクリプトと同じSCM回復設定です。</summary>
    public static string WindowsRecoveryActions => "restart/5000/restart/15000/restart/60000";

    /// <summary>SCMの失敗回数をリセットする無障害期間です。</summary>
    public static TimeSpan WindowsRecoveryReset => TimeSpan.FromDays(1);
}
