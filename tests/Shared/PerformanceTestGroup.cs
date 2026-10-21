using Xunit;

namespace ReArtWord.Contents.BackgroundService.Testing.Performance;

/// <summary>同じテストプロセスの割当量やCPU時間へ、別fixtureの負荷が混ざることを防ぎます。</summary>
// 抑制理由: xUnitが外部から発見するCollectionDefinitionにはpublic型が必要です。
[CollectionDefinition("Performance", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "xUnit1027が要求する公開fixture定義です。")]
public sealed class PerformanceTestGroup;
