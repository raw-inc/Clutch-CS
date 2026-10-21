namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;

/// <summary>設定と実行時状態を別々に選択します。</summary>
public enum ESnapshotKind
{
    /// <summary>ワークフロー定義。</summary>
    Configuration,

    /// <summary>保存・復元・比較を宣言した実行時状態。</summary>
    Runtime,
}
