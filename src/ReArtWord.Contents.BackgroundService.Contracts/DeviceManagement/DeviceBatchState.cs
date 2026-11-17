using ReArtWord.Contents.BackgroundService.Contracts.Execution;
namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>ClientHostが保持する一括変更の進捗です。</summary>
/// <param name="Request">受け付けた固定計画。</param>
/// <param name="CreatedAt">受付時刻。</param>
/// <param name="Running">処理を所有しているか。</param>
/// <param name="Results">計画と同じ順序の結果。nullは未開始。</param>
public sealed record DeviceBatchState(DeviceBatchRequest Request, DateTimeOffset CreatedAt, bool Running, IReadOnlyList<DeviceChangeResult?> Results)
{
    /// <summary>初回に選んだ派生IDの生成方式です。未指定の旧要求は旧方式のまま扱います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
