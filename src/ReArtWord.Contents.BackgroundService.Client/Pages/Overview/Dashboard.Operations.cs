using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Overview;

/// <summary>対象を固定した保存・実行・管理操作を要求します。</summary>
public sealed partial class Dashboard
{
    private ConfigurationImpactPreview? activationImpact;

    /// <summary>選択したピアの明示離脱を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task DetachAsync() => GuardAsync(async () =>
    {
        result = await Api.PostAsync<JsonElement>("/api/cluster/detach", new
        {
        }, cancellationToken: lifetime.Token).ConfigureAwait(true);
        message = "Detachment requested. The peer remains outside normal cluster targets until rejoined.";
        await Connection.RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    });

    /// <summary>選択したピアの同期を経由する再参加を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RejoinAsync() => GuardAsync(async () =>
    {
        result = await Api.PostAsync<JsonElement>("/api/cluster/rejoin", new
        {
        }, cancellationToken: lifetime.Token).ConfigureAwait(true);
        message = "Rejoin requested. Synchronization and readiness are confirmed separately.";
        await Connection.RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    });

    /// <summary>指定した保存済みワークフローの一回の実行を記録して要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RunAsync(WorkflowListSummary item) => GuardAsync(async () =>
    {
        if (!ListIsCurrent)
        {
            throw new InvalidOperationException(Ui["The connection changed. Refresh before operating."]);
        }

        WorkflowRunRequest? request = new WorkflowRunRequest(Guid.NewGuid(), item.Id!, item.EffectiveRevision);
        await Journal.StartAsync(request.OperationId, Ui["Manual run"], "/api/runs", "/api/runs/" + request.OperationId, request, cancellationToken: lifetime.Token).ConfigureAwait(true);
        message = Ui.Format("Accepted {0}: {1}", request.OperationId, OperationJournal.Outcome(Journal.LastResult));
    });

    /// <summary>保存した版を将来の入力へ反映する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task ActivateAsync() => GuardAsync(async () =>
    {
        WorkflowListSummary? item = activation!;
        if (activationImpact is null || !string.Equals(activationImpact.TargetId, item.Id, StringComparison.Ordinal)
            || !string.Equals(activationImpact.SavedRevision, item.SavedRevision, StringComparison.Ordinal))
        {
            return;
        }
        ActivationRequest? request = new ActivationRequest(Guid.NewGuid(), item.Id!, item.SavedRevision, true, activationImpact.Token);
        result = await Api.PostAsync<JsonElement>("/api/workflows/activate", request, cancellationToken: lifetime.Token).ConfigureAwait(true);
        activation = null;
        await RefreshAsync().ConfigureAwait(true);
    });

    /// <summary>入力受付の停止と受入済み処理の取消方針を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task StopSourceAsync(SourceView source, bool cancel) => GuardAsync(async () =>
    {
        if (!source.CanStop)
        {
            return;
        }
        NodeResult? value = await Api.PostAsync<NodeResult>("/api/sources/stop", new SourceStopRequest(source.WorkflowId, source.NodeId, cancel, source.Id, source.PeerInstanceId), cancellationToken: lifetime.Token).ConfigureAwait(true);
        result = JsonSerializer.SerializeToElement(value, ContractJson.Options);
        message = Ui.Format("{0}: {1}", cancel ? Ui["Cancel accepted work too"]
                                               : Ui["Stop intake"], value?.Outcome);
        await Connection.RefreshAsync(cancellationToken: lifetime.Token).ConfigureAwait(true);
    });

    /// <summary>登録済みの秘密参照名を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task SecretsAsync() => GuardAsync(async () => secretNames = await Api.GetAsync<List<string>>("/api/secrets", cancellationToken: lifetime.Token).ConfigureAwait(true) ?? []);

    /// <summary>秘密登録の入力欄を閉じて入力値を消去します。</summary>
    private void CloseSecret()
    {
        secretValue = string.Empty;
        secretOpen = false;
    }

    /// <summary>入力された秘密を参照名で保存する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task SaveSecretAsync() => GuardAsync(async () =>
    {
        try
        {
            await Api.PutAsync<JsonElement>("/api/secrets", new SecretChangeRequest(secretName, secretValue), cancellationToken: lifetime.Token).ConfigureAwait(true);
            message = "Secret persistence confirmed.";
        }
        finally
        {
            CloseSecret();
        }
    });
}
