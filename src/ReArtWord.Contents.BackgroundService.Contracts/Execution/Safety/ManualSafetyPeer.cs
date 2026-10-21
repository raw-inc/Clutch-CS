namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>続行の承認時に通常参加していた実体です。</summary>
public sealed record ManualSafetyPeer(string PeerId, Guid InstanceId);
