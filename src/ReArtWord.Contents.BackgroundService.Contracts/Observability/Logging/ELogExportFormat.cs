namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>検索結果の保存形式です。</summary>
public enum ELogExportFormat
{
    /// <summary>テキスト・JSON・取得結果をまとめたZIPです。</summary>
    Zip,

    /// <summary>詳細情報を含む一つのUTF-8ログファイルです。</summary>
    Log,
}
