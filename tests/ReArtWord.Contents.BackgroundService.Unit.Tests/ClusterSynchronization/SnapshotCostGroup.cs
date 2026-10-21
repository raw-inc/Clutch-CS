using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>非同期書出しの全プロセス割当量へ、別fixtureの負荷を混ぜません。</summary>
// 抑制理由: xUnitが外部から発見するCollectionDefinitionには公開型が必要です。
[CollectionDefinition("SnapshotCost", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "xUnit1027が要求する公開fixture定義です。")]
public sealed class SnapshotCostGroup;
