using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>登録の構造化フォームを表示し、保存を所有Peerへ委譲します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントの宣言と一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class ApplicationRegistrationEditor
{
    private ApplicationRegistrationDraft draft = new(null);
    private ProcessDefinition? loaded;
    private bool initialized;
    private bool browsing;
    private Func<string>? problem;

    /// <summary>編集を開始した保存版です。</summary>
    [Parameter]
    public ProcessDefinition? Definition
    {
        get;
        set;
    }

    /// <summary>対象を明示するホスト・クラスタ・Peer表示です。</summary>
    [Parameter]
    public string Owner
    {
        get;
        set;
    } = string.Empty;

    /// <summary>アプリ登録を所有する閲覧先クラスタです。</summary>
    [Parameter]
    public string ClusterId
    {
        get; set;
    } = string.Empty;

    /// <summary>ファイル参照にも登録と同じPeerを使用します。</summary>
    [Parameter]
    public string PeerId
    {
        get; set;
    } = string.Empty;

    /// <summary>実保存中の再操作を防ぎます。</summary>
    [Parameter]
    public bool Busy
    {
        get;
        set;
    }

    /// <summary>検証した定義を保存元へ返します。</summary>
    [Parameter]
    public EventCallback<ProcessDefinition> Save
    {
        get;
        set;
    }

    /// <summary>編集を破棄します。</summary>
    [Parameter]
    public EventCallback Cancel
    {
        get;
        set;
    }

    /// <summary>定期観測では編集中のフォームを作り直しません。</summary>
    protected override void OnParametersSet()
    {
        if (!initialized || !ReferenceEquals(loaded, Definition))
        {
            initialized = true;
            loaded = Definition;
            draft = new(Definition);
            browsing = false;
        }
    }

    /// <summary>選択したパスだけを下書きへ反映し、保存・起動は別の明示操作にします。</summary>
    private void ChooseExecutable(string path)
    {
        draft.Executable = path;
        browsing = false;
    }

    /// <summary>入力形式の問題をフォーム内へ返して編集値を維持します。</summary>
    /// <returns>確認できた保存要求です。</returns>
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        problem = null;
        try
        {
            await Save.InvokeAsync(draft.Build()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            problem = Ui.DeferredException(exception);
        }
    }

    /// <summary>日曜0から土曜6を共通の曜日指定へ反映します。</summary>
    private static void SetDay(ApplicationScheduleDraft schedule, int day, bool enabled)
    {
        if (enabled)
        {
            schedule.Days.Add(day);
        }
        else
        {
            schedule.Days.Remove(day);
        }
    }

    /// <summary>数値の保存契約とは別に曜日を表示します。</summary>
    /// <returns>翻訳可能な曜日名です。</returns>
    private static string DayName(int day) => ((DayOfWeek)day).ToString();
}
