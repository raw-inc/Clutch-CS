using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>保存・共有した定義の一版です。</summary>
/// <param name="Definition">定義。</param>
/// <param name="Revision">定義内容の版。</param>
/// <param name="SavedAt">保存開始時刻。</param>
/// <param name="ActivationPolicy">保存後の反映方針。</param>
/// <param name="LocalOnly">明示離脱中のローカル版で、共有済みではないことを示します。</param>
public sealed record StoredWorkflow(WorkflowDefinition Definition, string Revision, DateTimeOffset SavedAt, EActivationPolicy ActivationPolicy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LocalOnly = false);
