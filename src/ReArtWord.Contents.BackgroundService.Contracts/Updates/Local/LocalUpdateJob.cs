using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;

/// <summary>standaloneUpdaterが一回だけ処理する固定された更新入力です。</summary>
/// <param name="OperationId">再適用を防ぐ一回の識別子です。</param>
/// <param name="PeerId">保持するPeer識別子です。</param>
/// <param name="PreviousInstanceId">旧常駐実体です。</param>
/// <param name="PreviousProcess">停止対象のPIDと起動時刻です。</param>
/// <param name="Manifest">適用するリリース契約です。</param>
/// <param name="PayloadRoot">検証済みペイロードの絶対rootです。</param>
/// <param name="Options">この一回へ固定された操作rootとサービス方針です。</param>
/// <param name="ApplicationArguments">再起動時に保持するconfig指定等です。</param>
/// <param name="LauncherExecutablePath">dotnet等の明示launcher。nullはentryPointを直接実行します。</param>
/// <param name="LauncherArguments">entryPointの前に置く固定launcher引数です。</param>
/// <param name="RollbackOf">明示復元する成功更新の識別子です。</param>
public sealed record LocalUpdateJob(Guid OperationId, string PeerId, Guid PreviousInstanceId, ProcessIdentity PreviousProcess, ReleaseManifest Manifest, string PayloadRoot, UpdatesOptions Options, IReadOnlyList<string> ApplicationArguments, string? LauncherExecutablePath = null, IReadOnlyList<string>? LauncherArguments = null, Guid? RollbackOf = null);
