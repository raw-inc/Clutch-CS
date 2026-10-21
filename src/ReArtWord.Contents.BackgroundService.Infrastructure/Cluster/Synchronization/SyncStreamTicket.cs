namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>固定した書出しだけを読む、短命で推測不能な接続許可です。</summary>
internal sealed record SyncStreamTicket(int Port, Guid Token);
