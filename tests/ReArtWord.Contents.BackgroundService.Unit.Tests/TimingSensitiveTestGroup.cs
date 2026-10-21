using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests;

/// <summary>実時間の処理期限を検証する間は、別の試験による処理待ちを混ぜません。</summary>
// 抑制理由: xUnitが検出するコレクション定義には公開型が必要です。
[CollectionDefinition("TimingSensitive", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "xUnit1027が要求する公開コレクション定義です。")]
public sealed class TimingSensitiveTestGroup;
