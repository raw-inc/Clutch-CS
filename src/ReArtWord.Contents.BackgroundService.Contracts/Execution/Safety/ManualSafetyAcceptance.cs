namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>受付を直列化した実体内の順番です。分断側との全順序を意味しません。</summary>
public sealed record ManualSafetyAcceptance(string PeerId, Guid InstanceId, long Sequence);
