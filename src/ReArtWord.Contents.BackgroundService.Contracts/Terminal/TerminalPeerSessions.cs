using ReArtWord.Contents.BackgroundService.Contracts.Cluster;

namespace ReArtWord.Contents.BackgroundService.Contracts.Terminal;

/// <summary>一覧照会の確認状態と、最後に取得できた共有セッションです。</summary>
/// <param name="ClusterId">対象クラスタです。</param>
/// <param name="Peer">観測されたPeer実体です。</param>
/// <param name="Sessions">最大16件のセッションです。</param>
/// <param name="Confirmed">今回の応答を確認できたかです。</param>
public sealed record TerminalPeerSessions(string ClusterId, PeerInfo Peer, IReadOnlyList<TerminalView> Sessions, bool Confirmed);
