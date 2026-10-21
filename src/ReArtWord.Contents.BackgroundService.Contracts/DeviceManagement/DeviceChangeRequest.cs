namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;

/// <summary>変更前の設定版と対象実体を固定した一回の変更です。</summary>
/// <param name="OperationId">再照会する元の操作ID。</param>
/// <param name="HostId">変更するPC。</param>
/// <param name="InstanceId">変更するCore実体。</param>
/// <param name="Action">固定された変更の種類。</param>
/// <param name="Read">変更前の照会対象。</param>
/// <param name="ExpectedRevision">利用者が確認した内容版。</param>
/// <param name="Change">変更種類に対応する入力。</param>
/// <param name="AcknowledgeDisconnect">通信への影響を確認したか。</param>
public sealed record DeviceChangeRequest(Guid OperationId, string HostId, Guid InstanceId, EDeviceAction Action, DeviceReadRequest Read, string ExpectedRevision, System.Text.Json.JsonElement Change, bool AcknowledgeDisconnect = false);
