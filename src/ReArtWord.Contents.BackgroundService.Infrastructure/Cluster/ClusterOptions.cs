namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

/// <summary>通信インターフェースと参加条件を明示する設定です。</summary>
/// <param name="Id">クラスタ識別子。</param>
/// <param name="PeerId">永続Peer識別子。</param>
/// <param name="HostId">端末識別子。</param>
/// <param name="BindAddress">指定IPv4。</param>
/// <param name="BroadcastAddress">指定ネットワークのブロードキャスト先。</param>
/// <param name="Port">UDP共通ポート。</param>
/// <param name="Priority">Coordinator希望順位。</param>
/// <param name="NewCluster">明示的新規クラスタ初期化。</param>
/// <param name="DataPath">参加情報保存先。</param>
public sealed record ClusterOptions(string Id, string PeerId, string HostId, string BindAddress, string BroadcastAddress, int Port, int Priority, bool NewCluster, string DataPath)
{
    /// <summary>初回選択したNICの永続識別子です。省略時は指定IPから一意に確定します。</summary>
    public string? InterfaceId
    {
        get; init;
    }

    /// <summary>同じ明示IPで受け付けるログ転送用TCPポートです。</summary>
    public int? LogPort
    {
        get; init;
    }

    /// <summary>従来配送の互換試験でも、同じ保存・実行受付を使用します。</summary>
    internal bool PreferRequestStream
    {
        get; init;
    } = true;

    /// <summary>生存通知の周期です。</summary>
    public TimeSpan HeartbeatInterval
    {
        get; init;
    } = TimeSpan.FromSeconds(1);

    /// <summary>参加状態を利用不能へ移す無通信時間です。</summary>
    public TimeSpan PeerTimeout
    {
        get; init;
    } = TimeSpan.FromSeconds(5);

    /// <summary>一要求の配送確認上限です。</summary>
    public TimeSpan RequestTimeout
    {
        get; init;
    } = TimeSpan.FromSeconds(10);

    /// <summary>fragmentを含む一メッセージの最大バイト数です。</summary>
    public int MaximumMessageBytes
    {
        get; init;
    } = 4 * 1024 * 1024;

    /// <summary>固定書出しを保持する期限です。断片の再送では延長しません。</summary>
    public TimeSpan SyncSessionLifetime
    {
        get; init;
    } = TimeSpan.FromMinutes(5);

    /// <summary>一つの同期に使用するローカル作業ファイルの上限です。UDP一メッセージ上限とは別です。</summary>
    public long MaximumSyncSnapshotBytes
    {
        get; init;
    } = 1024L * 1024 * 1024;

    /// <summary>固定書出しと受入同期をそれぞれ同時に保持する最大数です。</summary>
    public int MaximumSyncSessions
    {
        get; init;
    } = 4;
}
