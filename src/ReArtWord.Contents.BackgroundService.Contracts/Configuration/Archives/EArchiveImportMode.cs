namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;

/// <summary>復元先にだけあるワークフローの扱いです。</summary>
public enum EArchiveImportMode
{
    /// <summary>同じIDを上書きし、復元先にだけある定義は残します。</summary>
    Merge,

    /// <summary>復元先にだけある定義を削除し、取り込み元にそろえます。</summary>
    Replace,
}
