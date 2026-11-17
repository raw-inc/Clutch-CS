using ReArtWord.Contents.BackgroundService.Contracts.Execution;
namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>画面はMACを再指定せず、確認済みのホスト設定で起動を要求します。</summary>
/// <param name="OperationId">明示操作を一回に固定するIDです。</param>
/// <param name="HostId">対象のPCです。</param>
public sealed record HostWakeStart(Guid OperationId, string HostId)
{
    /// <summary>初回に選んだ派生IDの生成方式です。未指定の旧要求は旧方式のまま扱います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
