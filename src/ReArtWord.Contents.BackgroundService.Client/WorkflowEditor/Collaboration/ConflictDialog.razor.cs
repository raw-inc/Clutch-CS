using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>草稿の競合箇所ごとに採用する候補を選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class ConflictDialog
{
    /// <summary>ダイアログを表示するかどうかです。</summary>
    [Parameter]
    public bool Open
    {
        get;
        set;
    }

    /// <summary>比較対象となる草稿の競合一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<EditConflict> Conflicts
    {
        get;
        set;
    } = [];

    /// <summary>親画面へ表示終了を通知します。</summary>
    [Parameter]
    public EventCallback Close
    {
        get;
        set;
    }

    /// <summary>選択した競合候補を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<IReadOnlyDictionary<string, int>> Apply
    {
        get;
        set;
    }

    /// <summary>共通比較元が失われた場合は全文候補から選びます。</summary>
    private bool HasWholeConflict => Conflicts.Any(item => item.Path.Length == 0);

    private readonly Dictionary<string, int> choices = new(StringComparer.Ordinal);
    private string? problem;
    private string fingerprint = string.Empty;

    /// <summary>親から渡された値に合わせて表示用の状態を更新します。</summary>
    protected override void OnParametersSet()
    {
        string? next = ContractJson.Hash(Conflicts);
        if (!string.Equals(next, fingerprint, StringComparison.Ordinal))
        {
            fingerprint = next;
            choices.Clear();
        }
    }

    /// <summary>指定した候補を現在の選択へ反映します。</summary>
    private void Choose(string path, ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int value))
        {
            choices[path] = value;
        }
        else
        {
            choices.Remove(path);
        }
    }

    /// <summary>比較する候補値を表示用の文字列へ変換します。</summary>
    /// <returns>削除・未確認・明示nullを区別した候補の表示です。</returns>
    private string Show(JsonElement? value, bool? exists) => exists == false ? Ui["(deleted or missing)"]
                                                                             : value is null && exists is null ? Ui["(null or deletion is unspecified)"]
                                                                                                               : value?.GetRawText() ?? "null";

    /// <summary>確認した選択内容に基づく操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ApplyAsync()
    {
        if (Conflicts.Any(conflict => (!HasWholeConflict || conflict.Path.Length == 0) && !choices.ContainsKey(conflict.Path)))
        {
            problem = Ui["Choose a value for every item."];
            return;
        }

        problem = null;
        await Apply.InvokeAsync(choices.ToDictionary()).ConfigureAwait(true);
    }
}
