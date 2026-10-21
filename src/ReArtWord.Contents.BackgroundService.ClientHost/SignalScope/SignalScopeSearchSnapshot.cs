namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>検索開始時の範囲と設定です。ローリングされた原本を保持しません。</summary>
internal readonly record struct SignalScopeSearchSnapshot(long RequestId, long Generation, long FirstId, long LastId, SignalScopeQuery Query, string HostName, IReadOnlyDictionary<string, string> Names);
