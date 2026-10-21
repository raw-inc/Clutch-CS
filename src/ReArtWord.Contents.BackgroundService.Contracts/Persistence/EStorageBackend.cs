namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence;

/// <summary>既存の永続保存媒体です。</summary>
public enum EStorageBackend
{
    /// <summary>SQLiteの原子的な取引です。</summary>
    Sqlite,

    /// <summary>排他と原子的なファイル置換を使用するJSONです。</summary>
    Json,
}
