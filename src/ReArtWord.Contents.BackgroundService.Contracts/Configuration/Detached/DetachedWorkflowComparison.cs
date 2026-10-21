using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>ローカル変更の元版と再参加後共有版を、既存の項目比較へ渡します。</summary>
public static class DetachedWorkflowComparison
{
    /// <summary>新規同名作成と共有側削除も明示選択なしに上書きしません。</summary>
    /// <returns>統合候補または選択待ちです。</returns>
    public static WorkflowMergeResult Merge(StoredWorkflow? baseline, StoredWorkflow? current, StoredWorkflow local, Guid operationId, IReadOnlyDictionary<string, EEditResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(resolutions);
        if (baseline is null && current is null)
        {
            return new(local.Definition, [], [], null);
        }
        if (baseline is null || current is null)
        {
            string path = "/workflow";
            if (resolutions.TryGetValue(path, out EEditResolution choice))
            {
                if (choice == EEditResolution.UseEdited)
                {
                    return new(local.Definition, [], [], null);
                }
                if (choice == EEditResolution.KeepCurrent)
                {
                    return new(current?.Definition, [], [], null);
                }
            }
            return new(null, [new(path, Element(baseline?.Definition), Element(current?.Definition), Element(local.Definition), baseline is not null, current is not null, true)], [], null);
        }
        return WorkflowEditorMerge.Merge(baseline.Definition, current.Definition, new(operationId, baseline.Revision, local.Definition, resolutions));
    }

    /// <summary>削除候補を架空の空定義に変換しません。</summary>
    /// <returns>存在する定義の表示用値です。</returns>
    private static JsonElement? Element(WorkflowDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }
        return JsonSerializer.SerializeToElement(definition);
    }
}
