namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>このClientHostの操作履歴と、他の所有者から観測した読取り専用進捗を分離します。</summary>
public sealed record FleetUpdateOverview(IReadOnlyList<FleetUpdateView> Owned, IReadOnlyList<FleetUpdateObservation> Observed, bool Unavailable = false);
