using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>実行時状態または設定の取得対象です。</summary>
/// <param name="OperationId">取得操作ID。</param>
/// <param name="Target">対象範囲。</param>
/// <param name="Permanent">共通保持期間による削除から明示的に除外するかです。</param>
public sealed record SnapshotCaptureRequest(Guid OperationId, SnapshotTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Permanent = false);
