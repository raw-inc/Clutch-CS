namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration;

/// <summary>反映前に比較する一項目の値です。省略値を完全な内容と扱いません。</summary>
/// <param name="Path">比較対象の項目パス。</param>
/// <param name="Before">現在有効な値の表示。nullは項目なしです。</param>
/// <param name="After">保存候補の値の表示。nullは項目なしです。</param>
/// <param name="Omitted">値の表示を省略・制限したか。</param>
public sealed record ConfigurationDifference(string Path, string? Before, string? After, bool Omitted);
