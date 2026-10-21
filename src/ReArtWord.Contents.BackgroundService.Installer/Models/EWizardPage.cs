namespace ReArtWord.Contents.BackgroundService.Installer.Models;

/// <summary>表示順とは独立したウィザードの各段階です。</summary>
internal enum EWizardPage
{
    /// <summary>言語選択です。</summary>
    Language,

    /// <summary>動作選択です。</summary>
    Operation,

    /// <summary>アンインストールする本体とクライアントを選びます。</summary>
    Components,

    /// <summary>ライセンス同意です。</summary>
    License,

    /// <summary>一つの通信経路を選択します。</summary>
    Network,

    /// <summary>受信したクラスタから参加先または保守対象を選びます。</summary>
    Targets,

    /// <summary>ホストとピアの識別です。</summary>
    Identity,

    /// <summary>標準または高度な設定です。</summary>
    Options,

    /// <summary>権限と配置先です。</summary>
    Advanced,

    /// <summary>クライアント外観です。</summary>
    Appearance,

    /// <summary>実行前確認です。</summary>
    Review,

    /// <summary>確定した操作の進行状況です。</summary>
    Executing,

    /// <summary>実結果と終了です。</summary>
    Result,

    /// <summary>保守動作の選択です。</summary>
    Maintenance,
}
