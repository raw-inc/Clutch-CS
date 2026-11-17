using ReArtWord.Contents.BackgroundService.Contracts.Execution;
namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>当初対象と選択方法を固定した復元要求です。</summary>
/// <param name="OperationId">操作ID。</param>
/// <param name="Selections">対象ごとの選択。</param>
/// <param name="At">時点指定。対象ごとにこの時刻以下の最新を選びます。</param>
public sealed record SnapshotRestoreRequest(Guid OperationId, IReadOnlyList<SnapshotSelection> Selections, DateTimeOffset? At = null)
{
    /// <summary>初回に選んだ派生IDの生成方式です。未指定の旧要求は旧方式のまま扱います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
