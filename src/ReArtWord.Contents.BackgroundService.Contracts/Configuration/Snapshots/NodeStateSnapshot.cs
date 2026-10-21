using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>状態値と、取得時に確認した状態の利用契約です。</summary>
/// <param name="WorkflowId">状態の所有ワークフロー。</param>
/// <param name="Contract">状態利用契約の識別子。</param>
/// <param name="Values">自由型の状態値。</param>
public sealed record NodeStateSnapshot(string WorkflowId, string Contract, IReadOnlyDictionary<string, NodeValue> Values);
