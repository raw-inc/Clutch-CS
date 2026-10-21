using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.State;

/// <summary>保存が確定した状態の初期値、またはその後の変更です。Valueのnull参照は削除を示します。</summary>
public sealed record StateChange(string Key, string? Version, NodeValue? Value, DateTimeOffset Time, bool Initial = false);
