using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Configuration;

/// <summary>共有設定の編集・保存と実行先への反映を管理します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class ConfigurationPanel
{
    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    private ClusterOperationalState? state;
    private ConfigurationImpactPreview? activationImpact;
    private bool open;
    private bool restoreReadOnly;
    private Func<string>? message = () => string.Empty;
    private string timeZone = "UTC";
    private EFileConflictPolicy conflict = EFileConflictPolicy.Fail;
    private EFileInternalFailurePolicy failure = EFileInternalFailurePolicy.Keep;
    private EActivationPolicy policy = EActivationPolicy.Explicit;
    private ESafetyConfirmation confirmation = ESafetyConfirmation.None;
    private readonly List<GroupRow> groups = [];

    /// <summary>対象の現在値を取得して編集または選択画面を開きます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task OpenAsync()
    {
        try
        {
            state = await Api.GetAsync<ClusterOperationalState>("/api/configuration", cancellationToken: UiCancellation).ConfigureAwait(true);
            ClusterOperationalSettings? settings = state?.Saved?.Settings ?? state?.Effective?.Settings;
            timeZone = settings?.TimeZoneId ?? "UTC";
            conflict = settings?.FileConflictPolicy ?? EFileConflictPolicy.Fail;
            failure = settings?.FileInternalFailurePolicy ?? EFileInternalFailurePolicy.Keep;
            policy = state?.SavedActivationPolicy ?? EActivationPolicy.Explicit;
            confirmation = settings?.SafetyConfirmation ?? ESafetyConfirmation.None;
            groups.Clear();
            foreach (KeyValuePair<string, IReadOnlyList<string>> group in settings?.HostGroups ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                groups.Add(new()
                {
                    Name = group.Key,
                    Hosts = string.Join(", ", group.Value)
                });
            }

            open = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            message = Ui.Deferred("The saved operational settings could not be retrieved.");
        }
    }

    /// <summary>入力内容の保存を要求し、現時点の結果を画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SaveAsync()
    {
        if (restoreReadOnly)
        {
            return;
        }
        ClusterOperationalSettings settings;
        try
        {
            settings = ConfigurationForm.Build(timeZone, conflict, failure, groups.Select(group => (group.Name, group.Hosts))) with
            {
                SafetyConfirmation = confirmation,
                LiveWorkflowUpdates = state?.Saved?.Settings.LiveWorkflowUpdates ?? false,
                LogRetention = state?.Saved?.Settings.LogRetention,
                DataRetention = state?.Saved?.Settings.DataRetention
            };
        }
        catch (InvalidOperationException exception)
        {
            message = Ui.DeferredException(exception);
            return;
        }

        Guid operation = Guid.NewGuid();
        try
        {
            ClusterOperationalSaveRequest request = new(operation, settings, state?.Saved?.Revision, policy);
            JsonElement response = await Journal.StartAsync(operation, "Save operational settings", "/api/configuration/save", "/api/configuration/operations/" + operation, request, cancellationToken: UiCancellation).ConfigureAwait(true);
            ClusterOperationalSaveResult? saved = response.Deserialize<ClusterOperationalSaveResult>(ContractJson.Options);
            message = Ui.Deferred("Saved {0} / shared {1} / snapshot {2} / applied {3}", saved?.Saved.Outcome, saved?.Shared.Outcome, saved?.Snapshot.Outcome, saved?.Activation.Outcome);
            state = await Api.GetAsync<ClusterOperationalState>("/api/configuration", cancellationToken: UiCancellation).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or JSException or InvalidOperationException)
        {
            message = Ui.Deferred("Operation {0} is unconfirmed. Query it in operation history.", operation);
        }
    }

    /// <summary>保存した版を将来の入力へ反映する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ActivateAsync()
    {
        if (restoreReadOnly)
        {
            return;
        }

        if (state?.Saved is null || activationImpact is null || !string.Equals(activationImpact.SavedRevision, state.Saved.Revision, StringComparison.Ordinal))
        {
            return;
        }

        Guid operation = Guid.NewGuid();
        try
        {
            ClusterOperationalActivateRequest? request = new ClusterOperationalActivateRequest(operation, state.Saved.Revision, true, activationImpact.Token);
            await Journal.StartAsync(request.OperationId, "Apply operational settings", "/api/configuration/activate", "/api/configuration/operations/" + request.OperationId, request, cancellationToken: UiCancellation).ConfigureAwait(true);
            message = Ui.Deferred("Applied result: {0}", OperationJournal.Outcome(Journal.LastResult));
            state = await Api.GetAsync<ClusterOperationalState>("/api/configuration", cancellationToken: UiCancellation).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or JSException)
        {
            message = Ui.Deferred("Activation is unconfirmed. Query operation {0}.", operation);
        }
    }

    /// <summary>編集するホストグループ名と所属を保持します。</summary>
    private sealed class GroupRow
    {
        /// <summary>編集するホストグループの名前です。</summary>
        public string Name
        {
            get;
            set;
        } = string.Empty;

        /// <summary>グループに含めるホスト識別子の入力です。</summary>
        public string Hosts
        {
            get;
            set;
        } = string.Empty;

        /// <summary>編集中の行を識別する短い表示です。</summary>
        /// <returns>行の識別に必要な表示値です。</returns>
        public override string ToString() => Name;
    }
}
