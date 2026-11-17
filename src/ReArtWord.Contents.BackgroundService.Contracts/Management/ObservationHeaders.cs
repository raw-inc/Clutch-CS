namespace ReArtWord.Contents.BackgroundService.Contracts.Management;

/// <summary>表示用の日時と、応答の新旧・要求との対応を分けて伝えるHTTPヘッダーです。</summary>
public static class ObservationHeaders
{
    /// <summary>ブラウザーがHTTP要求ごとに発行し、ClientHostが応答へ返す照合IDです。</summary>
    public static string Request => "X-Contents-Observation-Request";

    /// <summary>ClientHostの同じ起動実体内で増加する応答番号です。</summary>
    public static string Sequence => "X-Contents-Observation-Sequence";

    /// <summary>ClientHostの起動実体です。</summary>
    public static string Instance => "X-Contents-Host-Instance";

    /// <summary>ClientHostが応答時に取得した表示用のUTC日時です。</summary>
    public static string ObservedAt => "X-Contents-Observed-At";
}
