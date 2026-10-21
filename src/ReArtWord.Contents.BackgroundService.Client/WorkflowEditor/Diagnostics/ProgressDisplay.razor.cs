using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Values;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>実行値と静的値で同じ進捗表示を使います。</summary>
// 抑制理由: Razor生成コードの公開宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor生成コードの公開宣言に合わせます。")]
public sealed partial class ProgressDisplay
{
    /// <summary>検証済みの二つの数値です。</summary>
    [Parameter]
    public ProgressValue? Value
    {
        get; set;
    }
}
