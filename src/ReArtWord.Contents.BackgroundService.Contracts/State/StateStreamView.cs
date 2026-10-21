namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>State本文を無制限に転送せず、表示可能な短い値と存在を通知します。</summary>
public sealed record StateStreamView(string Id, bool Exists, string Text, DateTimeOffset Time, bool Truncated = false, string? Problem = null);
