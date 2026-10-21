namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;

/// <summary>画面はMACを再指定せず、確認済みのホスト設定で起動を要求します。</summary>
/// <param name="OperationId">明示操作を一回に固定するIDです。</param>
/// <param name="HostId">対象のPCです。</param>
public sealed record HostWakeStart(Guid OperationId, string HostId);
