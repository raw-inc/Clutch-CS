using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;

/// <summary>保持した分岐の版と、公開可能な場合だけの内容です。</summary>
/// <param name="Version">候補版です。</param>
/// <param name="Value">候補値です。秘密値は公開しません。</param>
public sealed record StateConflictCandidate(string Version, NodeValue? Value);
