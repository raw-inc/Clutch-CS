using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>Coordinatorへ固定配送する共有状態の比較更新です。</summary>
/// <param name="Scope">ワークフロー範囲。</param>
/// <param name="Key">状態名。</param>
/// <param name="ExpectedVersion">期待する版。</param>
/// <param name="Value">新値、または削除。</param>
internal sealed record StateUpdate(string Scope, string Key, string? ExpectedVersion, NodeValue? Value);
