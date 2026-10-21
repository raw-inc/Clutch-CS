namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;

/// <summary>明示選択と現在の通信成立を区別する表示状態です。</summary>
/// <param name="Selected">選択した割当。</param>
/// <param name="Interfaces">クライアント自身のNIC一覧。</param>
/// <param name="Listening">指定条件で監視可能か。</param>
/// <param name="Problem">通信不能理由。</param>
/// <param name="Bindings">全同時監視の個別状態。旧単一NIC応答では省略できます。</param>
public sealed record ClientNetworkState(ClientNetworkBinding? Selected, IReadOnlyList<ClientNetworkInterface> Interfaces, bool Listening, string? Problem = null, IReadOnlyList<ClientNetworkStatus>? Bindings = null);
