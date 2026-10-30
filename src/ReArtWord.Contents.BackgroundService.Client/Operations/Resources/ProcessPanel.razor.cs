using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Resources;

/// <summary>所有ホストを明示して登録・監視設定・実プロセス操作を管理します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class ProcessPanel
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

    private readonly List<ProcessView> processes = [];
    private ApplicationGroupPanel? groupPanel;
    private ProcessView? selected;
    private ProcessView? removing;
    private ServiceStatus? target;
    private ProcessDefinition? editing;
    private string? editVersion;
    private string? cluster;
    private string? peer;
    private Func<string>? message;
    private JsonElement? result;
    private bool busy, refreshing, stopOpen, resolveOpen, registrationOpen, restarting;
    private string gracefulSeconds = "30", pid = string.Empty, startedAt = string.Empty, resolveMode = string.Empty;

    private string Owner => (Connection.Current?.Peers.FirstOrDefault(item => string.Equals(item.PeerId, peer, StringComparison.Ordinal))?.HostId ?? peer)
        + " / " + cluster + " / " + peer;

    /// <summary>表示開始時のクラスタとPeerを固定し、後続の操作先を明示します。</summary>
    /// <returns>初回一覧の取得です。</returns>
    // 抑制理由: Blazorの固定署名であり、I/Oへ画面所有の取消トークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。")]
    protected override Task OnInitializedAsync()
    {
        cluster = Api.SelectedClusterId;
        peer = Api.SelectedPeerId;
        return RefreshAsync(UiCancellation);
    }

    /// <summary>操作中の二重送信を防ぎ、通信失敗を操作結果として表示します。</summary>
    /// <returns>一回の画面操作です。</returns>
    private async Task GuardAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException or InvalidOperationException)
        {
            message = exception is InvalidOperationException or FormatException ? Ui.DeferredException(exception)
                                                                                : Ui.Deferred("The input format or API result could not be confirmed.");
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>編集中のフォームは維持し、同じ所有Peerの観測だけを更新します。</summary>
    /// <returns>最新の現在状態です。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (busy || refreshing)
        {
            return;
        }
        refreshing = true;
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            if (groupPanel is not null)
            {
                await groupPanel.RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            target = null;
            message = Ui.Deferred("The application owner could not be confirmed.");
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>表示と操作を、選択されたホスト上の登録に限定します。</summary>
    /// <returns>確認済みの現在版です。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (peer is null || cluster is null)
        {
            processes.Clear();
            return;
        }
        ServiceStatus? current = await Api.GetForPeerAsync<ServiceStatus>("/api/status", peer, cluster, cancellationToken).ConfigureAwait(true);
        List<ProcessView>? values = await Api.GetForPeerAsync<List<ProcessView>>("/api/processes", peer, cluster, cancellationToken).ConfigureAwait(true);
        if (current is null || values is null || !string.Equals(current.PeerId, peer, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The application owner could not be confirmed.");
        }
        target = current;
        processes.Clear();
        processes.AddRange(values.OrderBy(item => item.Definition.DisplayName, StringComparer.CurrentCulture));
        if (selected is not null)
        {
            selected = processes.FirstOrDefault(item => string.Equals(item.Definition.Id, selected.Definition.Id, StringComparison.Ordinal));
        }
    }

    /// <summary>明示したホストへ切り替える前に旧操作対象を破棄します。</summary>
    /// <returns>新しいホストの確認です。</returns>
    private Task SelectPeerAsync(string? next, CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        peer = next;
        target = null;
        selected = null;
        result = null;
        processes.Clear();
        await LoadAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>選択している登録へ一回だけ操作Nodeを送信します。</summary>
    /// <returns>同じ要求IDの実行結果です。</returns>
    private Task CommandAsync(string type, object settings, CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        ServiceStatus owner = target ?? throw UiError.InvalidOperation("The application owner could not be confirmed.");
        Guid id = Guid.NewGuid();
        RemoteExecutionRequest request = new(id, id, "manual", "manual", new NodeDefinition(type, type, JsonSerializer.SerializeToElement(settings, ContractJson.Options),
            Target: new(EExecutionScope.One, Peers: [owner.PeerId])), new Dictionary<string, NodeValue>(), owner.PeerId, owner.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5));
        NodeResult? response = await Api.PostForPeerAsync<NodeResult>("/api/nodes/execute", request, owner.PeerId, cluster, cancellationToken: token).ConfigureAwait(true);
        message = Ui.Deferred("Operation {0}: {1} {2}", id, response?.Outcome, response?.ReasonCode);
        result = JsonSerializer.SerializeToElement(response, ContractJson.Options);
        stopOpen = false;
        await LoadAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>停止方針を明示した操作だけを受付けます。</summary>
    /// <returns>停止または終了確認後の再起動です。</returns>
    private Task StopAsync(string policy, CancellationToken cancellationToken = default)
    {
        int seconds;
        try
        {
            seconds = FormInput.Integer(gracefulSeconds, "Graceful stop timeout (seconds)", 1, 3600);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            message = Ui.DeferredException(exception);
            return Task.CompletedTask;
        }
        return CommandAsync(restarting ? "process.restart"
                                       : "process.stop", new { processId = selected!.Definition.Id, failurePolicy = policy, gracefulTimeoutMs = seconds * 1000 }, cancellationToken);
    }

    /// <summary>新規または選択された登録の編集を、現在の所有Peerに固定します。</summary>
    private void Edit(ProcessView? item)
    {
        editing = item?.Definition;
        editVersion = item?.Version;
        registrationOpen = true;
    }

    /// <summary>確認した版へ保存し、競合や検証失敗ではフォームを閉じません。</summary>
    /// <returns>登録の比較保存結果です。</returns>
    private Task RegisterAsync(ProcessDefinition definition, CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        RegistrationResult response = await Api.PostForPeerAsync<RegistrationResult>("/api/processes/register", new ProcessRegistrationRequest(definition, editVersion), peer, cluster, cancellationToken: token).ConfigureAwait(true);
        if (response.Outcome == EOutcome.Succeeded)
        {
            registrationOpen = false;
            message = Ui.Deferred("Application registration saved.");
        }
        else
        {
            message = Ui.Deferred("Registration was not saved. Check the schedule, time zone, recovery policy and saved version.");
        }
        await LoadAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>削除対象の登録版を確認画面へ固定します。</summary>
    private void ConfirmRemoval(ProcessView item)
    {
        removing = item;
        message = null;
    }

    /// <summary>送信中の削除操作は維持し、それ以外は確認を取り消します。</summary>
    private void CloseRemoval()
    {
        if (!busy)
        {
            removing = null;
        }
    }

    /// <summary>確認画面を開いた時点の登録版だけを削除します。</summary>
    /// <returns>登録削除と一覧更新の完了です。</returns>
    private Task RemoveAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        if (removing is not { } item || refreshing || target is null)
        {
            return;
        }
        RegistrationResult response = await Api.PostForPeerAsync<RegistrationResult>("/api/processes/remove", new ProcessRemovalRequest(item.Definition.Id, item.Version), peer, cluster, cancellationToken: token).ConfigureAwait(true);
        if (response.Outcome != EOutcome.Succeeded)
        {
            message = Ui.Deferred("Registration was not removed. Refresh the list and check the current registration.");
            if (response.Outcome is null or EOutcome.Unknown)
            {
                message = Ui.Deferred("Removal could not be confirmed. Refresh the list to check the registration.");
            }
            return;
        }
        processes.RemoveAll(value => string.Equals(value.Definition.Id, item.Definition.Id, StringComparison.Ordinal));
        removing = null;
        selected = null;
        result = null;
        message = Ui.Deferred("Application registration removed.");
        await LoadAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>人が確認した実体だけで、結果不明の起動意図を解決します。</summary>
    /// <returns>未確定起動の確認結果です。</returns>
    private Task ResolveAsync(CancellationToken cancellationToken = default) => GuardAsync(async token =>
    {
        if (resolveMode.Length == 0)
        {
            throw UiError.InvalidOperation("Select the observed process result.");
        }
        ProcessIdentity? identity = null;
        string running = "running", offset = "(Z|[+-]\\d\\d:\\d\\d)$";
        if (string.Equals(resolveMode, running, StringComparison.Ordinal))
        {
            if (!DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) || !System.Text.RegularExpressions.Regex.IsMatch(startedAt, offset))
            {
                throw UiError.InvalidOperation("Enter the exact OS start time including its time zone.");
            }
            identity = new(FormInput.Integer(pid, "PID", 1), at);
        }
        ProcessOperationResult? response = await Api.PostForPeerAsync<ProcessOperationResult>("/api/processes/resolve-launch", new ResolveProcessLaunchRequest(selected!.Definition.Id, identity), peer, cluster, cancellationToken: token).ConfigureAwait(true);
        message = Ui.Deferred("Observed result: {0}", response?.Outcome);
        if (response?.Outcome == EOutcome.Succeeded)
        {
            resolveOpen = false;
        }
        await LoadAsync(token).ConfigureAwait(true);
    }, cancellationToken);

    /// <summary>状態を論理名の一覧に短く表示します。</summary>
    /// <returns>翻訳可能な状態ラベルです。</returns>
    private static string State(ProcessView item)
    {
        if (item.Observed.Status?.ReplacementDeadline is not null)
        {
            return "Waiting for self-restart";
        }
        if (item.Observed.Status?.ReplacementProblem is not null)
        {
            return "Unknown";
        }
        return item.Observed.Status?.Running switch { true => "Running", false => "Stopped", _ => "Unknown" };
    }

    /// <summary>登録操作の公開応答です。</summary>
    private readonly record struct RegistrationResult(EOutcome? Outcome);
}
