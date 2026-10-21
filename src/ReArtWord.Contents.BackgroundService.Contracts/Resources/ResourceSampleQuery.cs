namespace ReArtWord.Contents.BackgroundService.Contracts.Resources;

/// <summary>最新計測の詳細を、使用する画面に必要な範囲へ限定します。</summary>
/// <param name="Summary">Dashboard用の計測値だけを送信するかです。省略時は従来の全詳細を返します。</param>
public sealed record ResourceSampleQuery(bool Summary = false);
