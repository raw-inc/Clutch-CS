namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>保存後に新しい処理へ反映する条件です。</summary>
public enum EActivationPolicy
{
    /// <summary>保存と自動スナップショット成功後に反映します。</summary>
    Auto,

    /// <summary>明示反映操作を待ちます。</summary>
    Explicit,

    /// <summary>影響の確認を伴う明示反映を待ちます。</summary>
    ConfirmImpact,
}
