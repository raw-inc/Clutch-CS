using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>未保存の候補と、人の判断が必要な項目です。</summary>
public sealed record DraftView(string WorkflowId, string? BaseRevision, string SessionVersion, WorkflowDefinition Definition, IReadOnlyList<EditConflict> Conflicts, IReadOnlyList<ValidationIssue> Validation, DateTimeOffset UpdatedAt)
{
    /// <summary>この編集を自動保存する操作IDです。旧版の下書きでは未設定のまま旧方式を使います。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AutoApplyOperationId
    {
        get; init;
    }
}
