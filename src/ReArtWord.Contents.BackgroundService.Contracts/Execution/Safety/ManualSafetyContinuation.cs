namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

/// <summary>同じ障害回にだけ有効な一時続行の承認条件です。</summary>
public sealed record ManualSafetyContinuation(IReadOnlyList<ManualSafetyPeer> OriginalPeers, bool Singleton, IReadOnlyList<string> MissingPeerIds, bool AutomaticStopObserved = true);
