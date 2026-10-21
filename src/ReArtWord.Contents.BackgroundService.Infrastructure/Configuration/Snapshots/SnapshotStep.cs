using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>復元操作の一対象・一段階です。</summary>
/// <param name="OperationId">全復元操作。</param>
/// <param name="Target">実対象。</param>
/// <param name="Value">適用または補償する値。</param>
/// <param name="Configurations">同時選択した設定候補。</param>
/// <param name="RuntimeStates">同時選択した実行時状態。</param>
/// <param name="ConfigurationTargets">設定候補の明示ノード範囲。</param>
internal sealed record SnapshotStep(Guid OperationId, SnapshotTarget Target, NodeValue? Value, IReadOnlyDictionary<string, NodeValue>? Configurations = null, IReadOnlyDictionary<string, NodeValue>? RuntimeStates = null, IReadOnlyDictionary<string, SnapshotTarget>? ConfigurationTargets = null);
