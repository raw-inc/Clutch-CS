namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>設定スナップショットの範囲を実Peerと選択ノードへ展開する要求です。</summary>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="PeerIds">明示したPeer。</param>
/// <param name="HostIds">明示したHost。</param>
/// <param name="HostGroups">現在の共通Host Group。</param>
/// <param name="NodeIds">選択ノードまたはSubgraph。未指定時は指定実行先へ属するノードです。</param>
public sealed record SnapshotScopeRequest(string WorkflowId, IReadOnlyList<string>? PeerIds = null, IReadOnlyList<string>? HostIds = null, IReadOnlyList<string>? HostGroups = null, IReadOnlyList<string>? NodeIds = null);
