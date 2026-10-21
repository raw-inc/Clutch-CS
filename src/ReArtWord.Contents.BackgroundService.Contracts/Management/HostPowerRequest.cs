namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>確認画面で固定したPCとCore実体にだけ送る電源操作です。</summary>
public sealed record HostPowerRequest(Guid OperationId, string HostId, Guid InstanceId, EHostPowerAction Action,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? NotAfter = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ScheduledAt = null);

/// <summary>OSへ要求できる電源操作です。</summary>
public enum EHostPowerAction
{
    /// <summary>PCを再起動します。</summary>
    Restart,

    /// <summary>PCをシャットダウンします。</summary>
    Shutdown,
}
