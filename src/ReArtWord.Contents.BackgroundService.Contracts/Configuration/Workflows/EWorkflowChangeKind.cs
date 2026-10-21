namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>変更後の反映条件を選ぶための設定分類です。</summary>
public enum EWorkflowChangeKind
{
    /// <summary>表示名とノードの画面位置です。</summary>
    Appearance,

    /// <summary>ノードの追加・削除・種類と配線です。</summary>
    Structure,

    /// <summary>処理設定と定数入力です。</summary>
    Values,

    /// <summary>実行先、開始・待受・重複・安全方針です。</summary>
    Execution,

    /// <summary>詳細追跡と個人情報の指定です。</summary>
    Diagnostics,

    /// <summary>変更種類別の反映方針自体です。</summary>
    Policy,
}
