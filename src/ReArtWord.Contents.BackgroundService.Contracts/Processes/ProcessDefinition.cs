namespace ReArtWord.Contents.BackgroundService.Contracts.Processes;

/// <summary>明示登録されたアプリケーションの操作範囲と起動設定です。</summary>
/// <param name="Id">Peer内で不変のアプリケーション識別子です。</param>
/// <param name="Mode">管理対象と監視のみの境界です。</param>
/// <param name="ExecutablePath">明示実行ファイルの絶対パスです。</param>
/// <param name="Arguments">shell展開しない起動引数です。</param>
/// <param name="WorkingDirectory">明示作業ディレクトリの絶対パスです。</param>
/// <param name="ExistingProcess">既存プロセスを登録するときの再利用検出可能な識別子です。</param>
/// <param name="Recovery">アプリケーション別の復旧制限です。</param>
/// <param name="LogicalName">画面とWFで選択する論理名。未指定の既存登録はIdを表示します。</param>
/// <param name="AutomaticMonitoring">WFや画面が開いていない間も終了を監視するかです。</param>
/// <param name="Schedules">明示登録した自動起動予定です。</param>
/// <param name="RestartDetection">終了確定前の自律再起動確認です。未指定は即時に終了を確定します。</param>
/// <param name="Launch">起動セッションと表示方法です。未指定の既存登録はCoreアカウント・コンソール非表示を維持します。</param>
public sealed record ProcessDefinition(string Id, EProcessMode Mode, string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory,
    ProcessIdentity? ExistingProcess = null, ProcessRecoveryPolicy? Recovery = null, string? LogicalName = null, bool AutomaticMonitoring = false, IReadOnlyList<ProcessSchedule>? Schedules = null, ProcessRestartDetection? RestartDetection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProcessLaunchOptions? Launch = null)
{
    /// <summary>旧登録の識別子を保持したまま、操作画面では論理名を優先します。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(LogicalName) ? Id
                                                                        : LogicalName;
}
