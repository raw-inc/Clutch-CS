using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>保存結果へ昇格させない一時UI通知です。</summary>
/// <param name="Kind">登録済み通知種類。</param>
/// <param name="Value">プレゼンス・編集中状態。</param>
internal sealed record TransientPayload(string Kind, NodeValue Value);
