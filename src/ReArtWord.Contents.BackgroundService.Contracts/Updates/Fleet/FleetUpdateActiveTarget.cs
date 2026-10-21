namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

/// <summary>現在入替中の対象名と段階だけを共有します。</summary>
public sealed record FleetUpdateActiveTarget(string ClusterId, string PeerId, string HostId, string State);
