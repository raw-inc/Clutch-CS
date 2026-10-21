namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>手動更新の管理root、実行物、健康確認と共通停止方針です。</summary>
/// <param name="InstallationRoot">入替対象の絶対root。未設定なら更新を開始しません。</param>
/// <param name="StagingRoot">検証済みペイロード、Updater helper、操作履歴を置く独立rootです。</param>
/// <param name="BackupRoot">旧リリースを退避する独立rootです。</param>
/// <param name="UpdaterExecutablePath">installation外へ配布したstandaloneUpdater実行物です。</param>
/// <param name="ServiceKind">直接プロセスまたは明示OS サービス管理を選びます。</param>
/// <param name="ServiceName">OS サービスとして操作する明示識別子です。</param>
/// <param name="StopTimeoutMilliseconds">通常終了を確認する有限上限です。</param>
/// <param name="StartTimeoutMilliseconds">再起動とreadyを確認する有限上限です。</param>
/// <param name="ForceStopOnTimeout">通常終了しなかったときだけ明示した強制終了を許します。</param>
/// <param name="RollbackFailurePolicy">旧版復帰失敗時の保存済み既定方針です。</param>
/// <param name="InterfaceId">クラスタ管理通信に明示するNICです。</param>
/// <param name="LocalAddress">クラスタ管理通信にbindするIPv4です。</param>
/// <param name="ClusterId">観測する固定クラスタです。</param>
/// <param name="BroadcastAddress">明示ブロードキャスト先です。</param>
/// <param name="ManagementPort">Coreと共有するUDPportです。</param>
public sealed record UpdatesOptions(string? InstallationRoot = null, string? StagingRoot = null, string? BackupRoot = null, string? UpdaterExecutablePath = null, EUpdateServiceKind ServiceKind = EUpdateServiceKind.DirectProcess, string ServiceName = "ReArtWord.Contents.BackgroundService", int StopTimeoutMilliseconds = 30000, int StartTimeoutMilliseconds = 120000, bool ForceStopOnTimeout = false, EUpdateRollbackFailurePolicy RollbackFailurePolicy = EUpdateRollbackFailurePolicy.ExcludeFailed, string? InterfaceId = null, string? LocalAddress = null, string? ClusterId = null, string? BroadcastAddress = null, int ManagementPort = 0);
