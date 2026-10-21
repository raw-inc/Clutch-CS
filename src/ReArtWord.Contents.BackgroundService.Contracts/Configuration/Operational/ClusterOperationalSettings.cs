using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Retention;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>全Peerで共有する運用値です。NIC、保存パス、許可ルートは含みません。</summary>
/// <param name="TimeZoneId">クラスタ共通のタイムゾーン識別子です。</param>
/// <param name="FileConflictPolicy">ファイル競合時の共通方針です。</param>
/// <param name="FileInternalFailurePolicy">一台内のファイル処理失敗時の共通方針です。</param>
/// <param name="HostGroups">Group名からHost識別子への平らな対応です。</param>
/// <param name="SafetyConfirmation">人為的一時操作前の共通確認方針です。既定は追加確認なし。</param>
/// <param name="LogRetention">共有するログ保持方針。旧定義の未指定はローカル既定を維持します。</param>
/// <param name="LiveWorkflowUpdates">共有草稿の有効な変更を自動保存・反映するか。既定は明示操作です。</param>
/// <param name="DataRetention">ログと各種履歴の共通保持方針です。旧定義の未指定は移行時に既存方針を継承します。</param>
public sealed record ClusterOperationalSettings(string TimeZoneId, EFileConflictPolicy FileConflictPolicy, EFileInternalFailurePolicy FileInternalFailurePolicy, IReadOnlyDictionary<string, IReadOnlyList<string>> HostGroups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ESafetyConfirmation SafetyConfirmation = ESafetyConfirmation.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LogRetentionSettings? LogRetention = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LiveWorkflowUpdates = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DataRetentionSettings? DataRetention = null);
