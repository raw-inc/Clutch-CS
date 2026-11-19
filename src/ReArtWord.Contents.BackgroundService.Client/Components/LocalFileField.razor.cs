using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace ReArtWord.Contents.BackgroundService.Client.Components;

/// <summary>選択したファイル名を表示し、元の読込み処理に渡します。</summary>
// 抑制理由: Razor が生成する公開コンポーネントの宣言に合わせます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開コンポーネントの宣言に合わせます。")]
public sealed partial class LocalFileField
{
    /// <summary>ファイルを選ぶ用途です。</summary>
    [Parameter, EditorRequired]
    public string Label
    {
        get; set;
    } = string.Empty;

    /// <summary>選択できるファイルの種類です。</summary>
    [Parameter]
    public string Accept
    {
        get; set;
    } = string.Empty;

    /// <summary>読込み中の再選択を止めます。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>選んだファイルを読み込み元へ渡します。</summary>
    [Parameter]
    public EventCallback<InputFileChangeEventArgs> OnChange
    {
        get; set;
    }

    private string? fileName;

    /// <summary>選択した名前を表示し、ファイルを読み込みます。</summary>
    /// <returns>呼び出し元での読込み完了です。</returns>
    private Task SelectAsync(InputFileChangeEventArgs args, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileName = args.File.Name;
        return OnChange.InvokeAsync(args);
    }
}
