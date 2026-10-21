using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.History;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>一つのPeerを起動するための共通設定です。</summary>
/// <param name="Cluster">参加先と明示通信場所。</param>
/// <param name="DataDirectory">ローカル保存先。</param>
/// <param name="StorageBackend">sqliteまたはjson。</param>
/// <param name="TimeZone">クラスタ共通タイムゾーン。</param>
/// <param name="WorkflowPaths">初回に読み込むYAML定義。</param>
/// <param name="Files">読取り・変更を許可する範囲と失敗時方針。</param>
/// <param name="HostGroups">平らなHost Group。</param>
public sealed record CommonConfiguration(
    ClusterOptions Cluster,
    string DataDirectory,
    string StorageBackend,
    TimeZoneInfo TimeZone,
    IReadOnlyList<string> WorkflowPaths,
    FileSystemOptions Files,
    IReadOnlyDictionary<string, IReadOnlyList<string>> HostGroups)
{
    /// <summary>このCoreを宛先とする共有転送キューを受け付けるかです。</summary>
    public bool TransferQueueEnabled
    {
        get; init;
    } = true;

    /// <summary>キュー所有Core自身の監視NIC一覧JSONです。省略時はclusterの明示ネットワークです。</summary>
    public string? TransferQueueNetworksFile
    {
        get; init;
    }

    /// <summary>同時更新の整合性を維持する用途別の保存方式です。</summary>
    public PersistenceOptions Persistence
    {
        get; init;
    } = new(Enum.Parse<EStorageBackend>(StorageBackend, true));

    /// <summary>確定済みローカル履歴を圧縮する、Peerの保存領域の方針です。</summary>
    public HistoryArchiveOptions HistoryArchive
    {
        get; init;
    } = new();

    /// <summary>保存失敗と切断時の内部データ利用を用途ごとに指定します。</summary>
    public AvailabilityOptions Availability
    {
        get; init;
    } = new();

    /// <summary>明示離脱中のローカル操作に適用する用途別方針です。</summary>
    public DetachedOperationOptions Detached
    {
        get; init;
    } = new();

    /// <summary>初期共有設定へ導入する、一時操作前確認の共通既定です。</summary>
    public ESafetyConfirmation SafetyConfirmation
    {
        get; init;
    }

    /// <summary>管理対象プロセスの共通既定です。</summary>
    public ProcessOptions Processes
    {
        get; init;
    } = new();

    /// <summary>ワークフローに依存しない実資源収集です。</summary>
    public ResourceOptions Resources
    {
        get; init;
    } = new();

    /// <summary>明示的な手動更新の配置先と再起動確認です。</summary>
    public UpdatesOptions Updates
    {
        get; init;
    } = new();

    /// <summary>ログの保存、保持と明示TCP待受の設定です。</summary>
    public LogOptions Logs
    {
        get; init;
    } = new(Cluster.PeerId, Path.Combine(DataDirectory, "logs"), LocalAddress: Cluster.BindAddress, ListenPort: 0, TimeZoneId: TimeZone.Id);

    /// <summary>明示したFFmpeg実行物、または起動時PATHから解決するコマンド名です。</summary>
    public string FfmpegPath
    {
        get; init;
    } = "ffmpeg";

    /// <summary>明示したFFprobe実行物、または起動時PATHから解決するコマンド名です。</summary>
    public string FfprobePath
    {
        get; init;
    } = "ffprobe";
}
