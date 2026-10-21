namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>管理接続で発行する、一回限りの状態観測TCP接続です。</summary>
public sealed record StateStreamTicket(int Port, string Token, DateTimeOffset ExpiresAt);
