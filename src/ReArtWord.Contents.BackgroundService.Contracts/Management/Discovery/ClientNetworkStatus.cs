namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>明示した一つの通信割当の監視状態です。</summary>
/// <param name="Binding">使用するNIC・IP・ブロードキャスト・ポート。</param>
/// <param name="Listening">その割当で監視可能か。</param>
/// <param name="Problem">その割当だけの通信不能理由。</param>
public sealed record ClientNetworkStatus(ClientNetworkBinding Binding, bool Listening, string? Problem = null);
