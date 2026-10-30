using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>同一PCのグループ登録と、受付・進行・完了を分けた操作画面です。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと宣言を揃えます。
[SuppressMessage("Design", "CA1515", Justification = "Razorの公開コンポーネントです。")]
public sealed partial class ApplicationGroupPanel
{
    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    /// <summary>登録と操作を固定するクラスタです。</summary>
    [Parameter]
    public string ClusterId
    {
        get; set;
    } = string.Empty;

    /// <summary>同じPCの登録を所有するPeerです。</summary>
    [Parameter]
    public string PeerId
    {
        get; set;
    } = string.Empty;

    /// <summary>操作対象を確認する表示名です。</summary>
    [Parameter]
    public string Owner
    {
        get; set;
    } = string.Empty;

    /// <summary>所有Peerの状態を親画面で確認できたかです。</summary>
    [Parameter]
    public bool Available
    {
        get; set;
    }

    /// <summary>同じPeerに登録済みのアプリケーションです。</summary>
    // 抑制理由: BlazorのParameterは親コンポーネントから最新の不変一覧を受け取ります。
    [SuppressMessage("SourceQuality", "RAW170", Justification = "設定フォームの候補を親画面の更新に合わせて置換します。")]
    [Parameter]
    public IReadOnlyList<ProcessDefinition> Applications
    {
        get; set;
    } = [];

    private readonly List<RegisteredProcessGroup> groups = [];
    private ApplicationGroupDraft? draft;
    private string? version;
    private RegisteredProcessGroup? confirming, removing;
    private EProcessGroupCommand command;
    private Func<string>? message;
    private bool busy, refreshing;

    /// <summary>このコンポーネントの固定された所有Peerから初回一覧を読みます。</summary>
    /// <returns>初回読取りの完了です。</returns>
    // 抑制理由: Blazorの固定署名であり、画面の寿命トークンを使用します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override Task OnInitializedAsync() => RefreshAsync(UiCancellation);

    /// <summary>編集中の値を保ち、操作の進行表示だけを更新します。</summary>
    /// <returns>最新の公開状態です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (busy || refreshing || string.IsNullOrEmpty(PeerId) || string.IsNullOrEmpty(ClusterId))
        {
            return;
        }
        refreshing = true;
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            message = Ui.Deferred("Application group status could not be confirmed. Refresh to check again.");
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>操作中の重複送信を防ぎ、応答不明の操作を再送しません。</summary>
    /// <returns>一回の画面操作です。</returns>
    private async Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (busy || !Available)
        {
            return;
        }
        busy = true;
        message = null;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
            await LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException or InvalidOperationException)
        {
            message = exception is FormatException or InvalidOperationException ? Ui.DeferredException(exception)
                                                                                : Ui.Deferred("Application group status could not be confirmed. Refresh to check again.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>全グループを同じ照会結果へ入れ替えます。</summary>
    /// <returns>一覧の更新完了です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RegisteredProcessGroup> values = await Api.GetForPeerAsync<List<RegisteredProcessGroup>>("/api/process-groups", PeerId, ClusterId, cancellationToken).ConfigureAwait(true)
            ?? throw UiError.InvalidOperation("Application group status could not be confirmed. Refresh to check again.");
        groups.Clear();
        groups.AddRange(values);
    }

    /// <summary>編集対象の版と未保存値を固定します。</summary>
    private void Edit(RegisteredProcessGroup? item)
    {
        draft = new(item?.Definition);
        version = item?.Version;
        message = null;
    }

    /// <summary>他グループに所属していない管理対象だけを選択肢へ出します。</summary>
    /// <returns>追加可能な登録です。</returns>
    private IEnumerable<ProcessDefinition> Candidates() => Applications.Where(application => application.Mode == EProcessMode.Managed
        && draft?.Members.Any(member => string.Equals(member.Id, application.Id, StringComparison.Ordinal)) != true
        && !groups.Any(group => !string.Equals(group.Definition.Id, draft?.Id, StringComparison.Ordinal)
            && group.Definition.Members.Any(member => string.Equals(member.ApplicationId, application.Id, StringComparison.Ordinal))));

    /// <summary>選んだ既存登録を末尾へ追加します。</summary>
    private void Add(ChangeEventArgs args)
    {
        string? id = args.Value?.ToString();
        if (draft is not null && Candidates().Any(application => string.Equals(application.Id, id, StringComparison.Ordinal)))
        {
            draft.Members.Add(new(new(id!)));
        }
    }

    /// <summary>検証済みの設定を、編集開始時の版へ保存します。</summary>
    /// <returns>比較保存と表示更新の完了です。</returns>
    private Task SaveAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        if (draft is null)
        {
            return;
        }
        Saved response = await Api.PostForPeerAsync<Saved>("/api/process-groups/register", new ProcessGroupRegistrationRequest(draft.Build(), version), PeerId, ClusterId, cancellationToken: token).ConfigureAwait(true);
        if (response.Outcome == EOutcome.Succeeded)
        {
            draft = null;
            message = Ui.Deferred("Application group saved.");
        }
        else
        {
            message = Ui.Deferred(response.Outcome is null or EOutcome.Unknown ? "Application group status could not be confirmed. Refresh to check again."
                                                                               : "The group was not saved. Check membership, active operations and the saved version.");
        }
    }, cancellationToken);

    /// <summary>全体停止を伴う操作の対象を確認画面へ固定します。</summary>
    private void Confirm(RegisteredProcessGroup item, EProcessGroupCommand action)
    {
        confirming = item;
        command = action;
    }

    /// <summary>受付と完了を区別し、進行は一覧の状態へ表示します。</summary>
    /// <returns>一回の操作受付です。</returns>
    private Task CommandAsync(RegisteredProcessGroup item, EProcessGroupCommand action, CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        ProcessGroupOperationResult? response = await Api.PostForPeerAsync<ProcessGroupOperationResult>("/api/process-groups/command", new ProcessGroupCommandRequest(item.Definition.Id, action, item.Version), PeerId, ClusterId, cancellationToken: token).ConfigureAwait(true);
        confirming = null;
        message = response?.Outcome == EOutcome.Succeeded ? Ui.Deferred("Group operation accepted. Progress is shown in the list.")
                                                          : Ui.Deferred("Group operation was not accepted: {0}", Ui[response?.Reason ?? "Unknown"]);
        if (response?.Outcome is null or EOutcome.Unknown)
        {
            message = Ui.Deferred("Application group status could not be confirmed. Refresh to check again.");
        }
    }, cancellationToken);

    /// <summary>グループだけを削除し、アプリ登録は残します。</summary>
    /// <returns>期待した版への削除結果です。</returns>
    private Task RemoveAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        if (removing is not { } item)
        {
            return;
        }
        Saved response = await Api.PostForPeerAsync<Saved>("/api/process-groups/remove", new ProcessGroupRemovalRequest(item.Definition.Id, item.Version), PeerId, ClusterId, cancellationToken: token).ConfigureAwait(true);
        if (response.Outcome == EOutcome.Succeeded)
        {
            removing = null;
            message = Ui.Deferred("Application group removed.");
        }
        else
        {
            message = Ui.Deferred(response.Outcome is null or EOutcome.Unknown ? "Application group status could not be confirmed. Refresh to check again."
                                                                               : "The group was not removed. Check its current operation and saved version.");
        }
    }, cancellationToken);

    /// <summary>保存中以外は編集を閉じます。</summary>
    private void CloseEditor()
    {
        if (!busy)
        {
            draft = null;
        }
    }

    /// <summary>送信中以外は停止確認を閉じます。</summary>
    private void CloseConfirmation()
    {
        if (!busy)
        {
            confirming = null;
        }
    }

    /// <summary>削除中以外は確認を閉じます。</summary>
    private void CloseRemoval()
    {
        if (!busy)
        {
            removing = null;
        }
    }

    /// <summary>登録が残っていれば表示名を使用します。</summary>
    /// <returns>アプリの表示名または参照IDです。</returns>
    private string Name(string id) => Applications.FirstOrDefault(application => string.Equals(application.Id, id, StringComparison.Ordinal))?.DisplayName ?? id;

    /// <summary>利用者の確認待ちを除く進行中の操作かです。</summary>
    /// <returns>別の操作を重ねない場合はtrueです。</returns>
    private static bool Active(RegisteredProcessGroup item) => item.Status.Phase is not EProcessGroupPhase.Idle and not EProcessGroupPhase.Paused;

    /// <summary>復旧方針を選択肢と一覧で共通表示します。</summary>
    /// <returns>表示語のキーです。</returns>
    private static string Policy(EProcessGroupRecoveryMode mode) => mode == EProcessGroupRecoveryMode.EntireGroup ? "Stop all applications, then restart in order"
                                                                                                                  : "Restart only failed applications";

    /// <summary>操作受付後の進行と、自動復旧の抑止を表示します。</summary>
    /// <returns>表示語のキーです。</returns>
    private static string Phase(ProcessGroupStatus status) => status.Phase switch
    {
        EProcessGroupPhase.Stopping => "Stopping group applications",
        EProcessGroupPhase.Waiting => "Waiting for group startup",
        EProcessGroupPhase.Starting => "Starting group applications",
        EProcessGroupPhase.Paused => "Group operation paused",
        _ => status.RecoverySuppressed ? "Group recovery suspended"
                                       : "Group idle",
    };

    /// <summary>設定保存と削除の結果です。</summary>
    private readonly record struct Saved(EOutcome? Outcome);
}
