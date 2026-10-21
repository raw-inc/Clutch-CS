namespace ReArtWord.Contents.BackgroundService.Contracts.Resources;

/// <summary>取得不能をゼロへ丸めない一つの実測値です。</summary>
/// <param name="Name">安定したmetric名です。</param>
/// <param name="Target">machine、NIC、drive、GPU等の明示対象です。</param>
/// <param name="Value">実測値。取得不能ならnullです。</param>
/// <param name="Unit">値の単位です。</param>
/// <param name="Reason">取得不能または初回差分待ちの理由です。</param>
/// <param name="Label">OSで確認した対象の表示名。識別子の代わりには使いません。</param>
public sealed record ResourceMetric(string Name, string Target, double? Value, string Unit, string? Reason = null, string? Label = null);
