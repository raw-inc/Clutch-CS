using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>操作履歴を増やさず原子的に置換する、ローカル診断の一項目です。</summary>
/// <param name="Scope">peer:で始まるローカル用途です。</param>
/// <param name="Key">診断項目の識別子です。</param>
/// <param name="Value">この確定で保存する所有値です。</param>
public sealed record LocalSnapshotWrite(string Scope, string Key, NodeValue Value);
