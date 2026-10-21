namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>Peerまたは平らなHost Groupによる実行対象指定です。</summary>
/// <param name="Scope">対象ごとの実行回数。</param>
/// <param name="Peers">明示Peer集合。</param>
/// <param name="Groups">Host Group集合。</param>
/// <param name="SourcePeer">継続入力の取得開始時の発生元Peer実体へOne実行する明示指定。</param>
public sealed record TargetDefinition(
    EExecutionScope Scope,
    IReadOnlyList<string>? Peers = null,
    IReadOnlyList<string>? Groups = null,
    bool SourcePeer = false);
