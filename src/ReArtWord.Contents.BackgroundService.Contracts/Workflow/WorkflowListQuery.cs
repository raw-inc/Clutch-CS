namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>保存済みワークフローの軽量一覧をページで取得します。</summary>
/// <param name="Limit">最大件数。</param>
/// <param name="Cursor">前ページの固定長識別子ハッシュ。</param>
/// <param name="MaximumBytes">応答JSONのバイト上限。</param>
/// <param name="Ids">任意の識別子絞込み。最大64件。本文を取得せず表示名などを解決します。</param>
public sealed record WorkflowListQuery(int Limit = 50, string? Cursor = null, int MaximumBytes = 524288,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Ids = null);
