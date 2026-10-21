namespace ReArtWord.Contents.BackgroundService.Contracts.Retention;

/// <summary>同じ圧縮・削除方針で扱うログと履歴の種類です。</summary>
public enum ERetentionCategory
{
    /// <summary>詳細調査用のログです。</summary>
    DebugLog,

    /// <summary>通常動作のログです。</summary>
    InfoLog,

    /// <summary>警告のログです。</summary>
    WarnLog,

    /// <summary>失敗のログです。</summary>
    ErrorLog,

    /// <summary>継続不能な障害のログです。</summary>
    FatalLog,

    /// <summary>完了した要求の元結果です。</summary>
    RequestHistory,

    /// <summary>WFとノードの実行履歴です。</summary>
    ExecutionHistory,

    /// <summary>設定変更や更新などの管理操作履歴です。</summary>
    OperationHistory,

    /// <summary>共有状態を再構成する確定変更の履歴です。</summary>
    SharedHistory,

    /// <summary>WFの過去の保存版です。</summary>
    WorkflowVersions,

    /// <summary>手動・自動で取得した復元点です。</summary>
    Snapshots,
}
