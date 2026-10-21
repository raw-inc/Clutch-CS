using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>保存候補と実効版を、定義本文なしで示します。</summary>
/// <param name="Id">単体取得する識別子。巨大な識別子はnull。</param>
/// <param name="Name">表示名。</param>
/// <param name="SavedRevision">保存候補の版。</param>
/// <param name="EffectiveRevision">実稼働版。</param>
/// <param name="SavedAt">保存時刻。</param>
/// <param name="NodeCount">保存候補のノード数。</param>
/// <param name="Enabled">自動入力を有効にする保存候補か。</param>
/// <param name="IdentifierHash">識別子の固定長ハッシュ。</param>
/// <param name="DetailReason">表示文字列を省略した理由。</param>
/// <param name="SavedActivationPolicy">保存候補の実際の反映方針。</param>
/// <param name="LocalOnly">このPeerだけに保存された未共有版か。</param>
public sealed record WorkflowListSummary(string? Id, string Name, string SavedRevision, string? EffectiveRevision,
    DateTimeOffset SavedAt, int NodeCount, bool Enabled, string IdentifierHash, string? DetailReason = null, EActivationPolicy SavedActivationPolicy = EActivationPolicy.Auto, bool LocalOnly = false)
{
    /// <summary>定義本文を転送せず、公開境界があることをタブへ通知します。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSubworkflow
    {
        get; init;
    }

    /// <summary>実行を持たないWFDashboardの表示候補です。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDashboard
    {
        get; init;
    }
}
