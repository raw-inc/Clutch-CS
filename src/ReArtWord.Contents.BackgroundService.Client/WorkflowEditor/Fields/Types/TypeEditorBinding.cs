using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields.Types;

/// <summary>型エディターの不正な途中入力を、既存の保存禁止と診断へ接続します。</summary>
internal static class TypeEditorBinding
{
    /// <summary>入力欄を識別して無効な編集状態を保持します。</summary>
    /// <returns>親への検証状態の通知です。</returns>
    internal static Task InvalidAsync(EditorDocument model, string key, EventCallback<bool> changed, string? raw, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        model.SetInput(key, raw);
        return changed.InvokeAsync(model.HasInvalidInputs);
    }
}
