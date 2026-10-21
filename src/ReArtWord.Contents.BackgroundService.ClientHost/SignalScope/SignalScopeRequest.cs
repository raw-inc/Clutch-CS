namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>一接続のメモリ操作です。保存・副作用を伴う要求は含めません。</summary>
internal sealed record SignalScopeRequest(string Type, long RequestId = 0, long PacketId = 0, SignalScopeExpression? Expression = null,
    string? Mode = null, int? MaximumCount = null, long? MaximumBytes = null, string? HostName = null, SignalScopeName[]? Names = null);
