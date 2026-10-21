namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>実受信元と表示期限を持つ編集者です。</summary>
public sealed record PresenceView(string PeerId, Guid PeerInstanceId, PresenceUpdate Presence, DateTimeOffset ExpiresAt);
