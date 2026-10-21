namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence;

/// <summary>原子的な整合群を分割しない保存用途です。</summary>
public enum EStoragePurpose
{
    /// <summary>定義・変更履歴・スナップショット・編集保護などの設定です。</summary>
    Configuration,

    /// <summary>ノード状態・実行結果・共有運用操作履歴です。</summary>
    Runtime,

    /// <summary>登録アプリケーションの定義と故障・起動状態です。</summary>
    Processes,

    /// <summary>リソースとログのローカル診断情報です。</summary>
    Diagnostics,
}
