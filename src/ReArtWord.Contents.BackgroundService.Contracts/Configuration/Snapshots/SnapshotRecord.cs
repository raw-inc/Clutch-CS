using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>内容検査値付きの一対象スナップショットです。</summary>
/// <param name="Id">スナップショット識別子。</param>
/// <param name="TargetId">定義またはparticipant識別子。</param>
/// <param name="PeerId">取得元Peer。</param>
/// <param name="Kind">保存状態の種類。</param>
/// <param name="CapturedAt">取得時刻。</param>
/// <param name="Value">実取得値。</param>
/// <param name="Hash">内容検査値。</param>
/// <param name="NodeIds">設定の取得対象ノード集合。nullはワークフロー全体です。</param>
/// <param name="LocalOnly">離脱側でのみ保存され、クラスタ共有済みではないか。</param>
/// <param name="Permanent">明示的に永久保持するかです。既定は共通の保持期間に従います。</param>
public sealed record SnapshotRecord(string Id, string TargetId, string PeerId, ESnapshotKind Kind, DateTimeOffset CapturedAt, NodeValue Value, string Hash, IReadOnlyList<string>? NodeIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LocalOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Permanent = false);
