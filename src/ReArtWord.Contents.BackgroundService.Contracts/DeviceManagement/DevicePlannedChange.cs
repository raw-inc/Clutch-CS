using ReArtWord.Contents.BackgroundService.Contracts.Execution;
namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>一括操作の一対象と、その設定変更です。</summary>
/// <param name="Target">固定した通信先。</param>
/// <param name="Request">固定した変更内容。</param>
public sealed record DevicePlannedChange(DeviceHostTarget Target, DeviceChangeRequest Request)
{
    /// <summary>初回に選んだ派生IDの生成方式です。未指定の旧要求は旧方式のまま扱います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
