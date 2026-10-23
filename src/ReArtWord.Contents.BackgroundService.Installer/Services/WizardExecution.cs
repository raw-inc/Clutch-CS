using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;
using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>対象集合を固定して、一括操作を対象別結果へまとめます。</summary>
internal static class WizardExecution
{
    /// <summary>確認画面に入る前に実配置と保持条件を検証します。</summary>
    /// <returns>確認準備の完了です。</returns>
    public static async Task PrepareAsync(InstallationViewModel state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Operation == EInstallerOperation.Install)
        {
            string manifest = Path.Combine(state.Package, "core", "release-manifest.json");
            if (!File.Exists(manifest))
            {
                throw new InvalidDataException(state.Text("Setup files are missing. Extract the complete download and open Clutch-CS-Setup again.", "セットアップ用ファイルが見つかりません。ダウンロードしたファイルをすべて展開して、Clutch-CS-Setupを開き直してください。"));
            }
            if (!state.Advanced)
            {
                state.PerUser = false;
                state.Root = InstallationPolicy.DefaultRoot(state.Cluster, false);
            }
            InstallationSettings settings = state.Settings();
            if (settings.WebClient is { Install: true } client && !InstallationPolicy.ClientPortAvailable(client.Port))
            {
                throw new InvalidDataException(new Localization.InstallerText(state.Japanese)["WebClientPortInUse"]);
            }
            InstallationPolicy.Validate(settings);
            ValidateDestination(state, settings);
            if (!settings.NewCluster && !state.Observed.Any(target => target.Binding.Port == settings.Network.Port && string.Equals(target.ClusterId, settings.ClusterId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(state.Text("Search and choose an existing cluster.", "検索結果から参加するクラスターを選んでください。"));
            }
            if (state.Observed.Any(target => target.Binding.Port == settings.Network.Port && string.Equals(target.ClusterId, settings.ClusterId, StringComparison.Ordinal) && (settings.NewCluster || string.Equals(target.PeerId, settings.PeerId, StringComparison.Ordinal))))
            {
                throw new InvalidDataException(state.Text("This cluster or registration name is already in use.", "クラスター名または登録名が既に使われています。"));
            }
            return;
        }
        bool removeClient = state.Operation == EInstallerOperation.Uninstall && state.Components.RemoveWebClient;
        if (state.Operation == EInstallerOperation.Uninstall && !state.Components.RemoveService)
        {
            state.Selected.Clear();
        }
        if (removeClient)
        {
            WebClientInstallation selected = state.Components.Existing ?? throw new InvalidDataException(state.Text("The web client is not installed.", "Web クライアントはインストールされていません。"));
            WebClientInstallation current = await WebClientCatalog.ReadAsync(selected.Root, false, cancellationToken).ConfigureAwait(false);
            if (current != selected)
            {
                throw new InvalidDataException(state.Text("The web client installation has changed. Select it again.", "Web クライアントの状態が変わりました。削除する項目を選び直してください。"));
            }
        }
        if (state.Selected.Count == 0 && !removeClient && !(state.Operation == EInstallerOperation.Update && state.SelectedWebClients.Count != 0))
        {
            throw new InvalidDataException(state.Text("Select at least one target.", "対象を1つ以上選んでください。"));
        }
        if (state.Operation == EInstallerOperation.Repair && state.Repair.RestoreArchive)
        {
            await InstallerArchive.PrepareAsync(state, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (state.Operation == EInstallerOperation.Update && state.SelectedWebClients.Count != 0)
        {
            string payload = Path.Combine(state.Package, "client");
            Contracts.Updates.Releases.ReleaseManifest manifest = await Infrastructure.Updates.Local.LocalUpdateExecutor.ReadManifestAsync(payload, cancellationToken).ConfigureAwait(false);
            Infrastructure.Updates.Client.ClientUpdateExecutor.ValidateRelease(manifest);
            await Infrastructure.Updates.Releases.ReleaseVerifier.VerifyAsync(manifest, payload, cancellationToken).ConfigureAwait(false);
            foreach (ObservedWebClient client in state.SelectedWebClients.Where(client => client.Core is null))
            {
                _ = await WebClientUpdateProbe.ReadAsync(client.Installation, cancellationToken).ConfigureAwait(false);
            }
        }
        if (state.Operation == EInstallerOperation.Uninstall || (state.Operation == EInstallerOperation.Repair && state.Repair.Reinstall))
        {
            for (int index = 0; index < state.Selected.Count; index++)
            {
                ObservedTarget target = state.Selected[index];
                InstallationRecord record = await InstallerOperations.InspectAsync(target, cancellationToken).ConfigureAwait(false);
                state.Selected[index] = target with { Installation = record };
            }
        }
        if (state.Operation == EInstallerOperation.Repair && state.Repair.Reinstall && !state.KeepDatabase)
        {
            foreach (ObservedTarget target in state.Selected)
            {
                bool survivor = state.Observed.Any(other => other.Binding == target.Binding && string.Equals(other.ClusterId, target.ClusterId, StringComparison.Ordinal)
                    && !state.Selected.Any(selected => selected.Binding == other.Binding && string.Equals(selected.ClusterId, other.ClusterId, StringComparison.Ordinal) && string.Equals(selected.PeerId, other.PeerId, StringComparison.Ordinal)) && other.Peer?.Status == Contracts.Cluster.EPeerStatus.Ready);
                if (!survivor)
                {
                    throw new InvalidDataException(state.Text("To retrieve data, leave at least one working installation unselected in each cluster.", "データを取り直すには、各クラスターに正常に動作する対象を1つ以上残してください。"));
                }
            }
        }
    }

    /// <summary>対象別の結果を表示し、一つの失敗で結果全体を失いません。</summary>
    /// <returns>実行結果の表示です。</returns>
    public static async Task<WizardResult> ExecuteAsync(InstallationViewModel state, IProgress<OperationProgress> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(progress);
        if (state.Operation == EInstallerOperation.Repair && state.Repair.RestoreArchive)
        {
            return await InstallerArchive.ExecuteAsync(state, progress, cancellationToken).ConfigureAwait(false);
        }
        StringBuilder report = new();
        StringBuilder details = new();
        if (state.Operation == EInstallerOperation.Install)
        {
            InstallationSettings settings = state.Settings();
            ValidateDestination(state, settings);
            WebClientInstallation? readyClient = null;
            if (settings.WebClient is { } selection && (selection.Install || selection.ExistingRoot is not null))
            {
                SetupResult installedClient = await InstallerOperations.InstallClientAsync(new(Guid.NewGuid(), settings), Path.Combine(settings.PackageRoot, "updater"),
                    value => progress.Report(value with { Target = state.Text("Web client", "Web クライアント") }), cancellationToken).ConfigureAwait(false);
                if (installedClient.Outcome != EOutcome.Succeeded || installedClient.WebClient is not { } installed)
                {
                    return new(Outcome(state, installedClient.Outcome), Format(installedClient));
                }
                readyClient = installed;
                settings = settings with { WebClient = new(false, installed.Port, installed.Root, installed.RunAsService) };
            }
            SetupJob job = new(Guid.NewGuid(), ESetupAction.Install, settings);
            SetupResult result = await InstallerOperations.LocalAsync(job, Path.Combine(settings.PackageRoot, "updater"),
                value => progress.Report(value with { Target = settings.ClusterId + " / " + settings.PeerId }), cancellationToken).ConfigureAwait(false);
            return InstallResult(state, result, readyClient);
        }
        ObservedTarget[] targets = state.Selected.ToArray();
        if (state.Operation == EInstallerOperation.Update)
        {
            Dictionary<ObservedTarget, UpdatePeerResult> coreResults = [];
            IGrouping<(string ClusterId, Contracts.Management.Discovery.ClientNetworkBinding Binding), ObservedTarget>[] clusters = targets.GroupBy(target => (target.ClusterId, target.Binding)).ToArray();
            int index = 0;
            int completedSteps = 0;
            int totalSteps = 0;
            foreach (IGrouping<(string ClusterId, Contracts.Management.Discovery.ClientNetworkBinding Binding), ObservedTarget> cluster in clusters)
            {
                index++;
                string group = $"({index} / {clusters.Length}) {cluster.Key.ClusterId}";
                int currentSteps = 0;
                EOutcome outcome = EOutcome.Unknown;
                progress.Report(new("update-check", completedSteps, totalSteps, group));
                try
                {
                    Contracts.Updates.Cluster.ClusterUpdateStatus result = await InstallerOperations.UpdateAsync(cluster.ToArray(), Path.Combine(state.Package, "core"), state.AllowActive,
                        value =>
                        {
                            currentSteps = value.CompletedSteps;
                            totalSteps = value.TotalSteps / cluster.Count() * targets.Length;
                            string target = group;
                            if (value.Target is not null)
                            {
                                target += " / " + value.Target;
                            }
                            progress.Report(value with { CompletedSteps = completedSteps + currentSteps, TotalSteps = totalSteps, Target = target });
                        }, cancellationToken).ConfigureAwait(false);
                    outcome = result.Outcome;
                    report.AppendLine(cluster.Key.ClusterId + " · " + Outcome(state, result.Outcome));
                    details.AppendLine(cluster.Key.ClusterId + " · " + result.Outcome + " · " + result.Reason);
                    foreach (Contracts.Updates.UpdatePeerResult peer in result.Peers)
                    {
                        coreResults[cluster.Single(target => string.Equals(target.PeerId, peer.PeerId, StringComparison.Ordinal))] = peer;
                        report.AppendLine("  " + peer.PeerId + " · " + Outcome(state, peer.Outcome));
                        details.AppendLine("  " + peer.PeerId + " · " + peer.Outcome + " · " + peer.Phase + " · " + peer.Reason);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    report.AppendLine(cluster.Key.ClusterId + " · " + Outcome(state, EOutcome.Unknown));
                    details.AppendLine(exception.Message);
                }
                completedSteps += currentSteps;
                progress.Report(new("outcome-" + outcome, completedSteps, totalSteps, group));
            }
            WebClientInstallation? updatedClient = await UpdateClientsAsync(state, coreResults, report, details, progress, cancellationToken).ConfigureAwait(false);
            return new(report.ToString(), details.ToString(), updatedClient);
        }
        int completed = 0;
        foreach (ObservedTarget target in targets)
        {
            progress.Report(new("working", completed, targets.Length, target.DisplayName(state.Japanese)));
            try
            {
                SetupResult result;
                if (state.Operation == EInstallerOperation.Repair && !state.Repair.Reinstall)
                {
                    result = await AutomaticRepair.RunAsync(target, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ESetupAction action = ESetupAction.Reinstall;
                    if (state.Operation == EInstallerOperation.Uninstall)
                    {
                        action = ESetupAction.Uninstall;
                    }
                    InstallationRecord record = target.Installation!;
                    if (target.Peer is null)
                    {
                        SetupJob job = new(Guid.NewGuid(), action, record.Settings with { PackageRoot = state.Package }, record.InstallationId, state.KeepDatabase, state.KeepConfiguration, SurvivorConfirmed: !state.KeepDatabase);
                        result = await InstallerOperations.LocalAsync(job, Path.Combine(state.Package, "updater"),
                            value => progress.Report(value with { Target = target.DisplayName(state.Japanese) }), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await InstallerOperations.RemoteAsync(target, new RemoteSetupRequest(Guid.NewGuid(), action, record.InstallationId, state.KeepDatabase, state.KeepConfiguration, !state.KeepDatabase), cancellationToken).ConfigureAwait(false);
                    }
                }
                if (state.Operation == EInstallerOperation.Repair && result.Outcome == EOutcome.Succeeded
                    && target.Installation?.WebClient is { RunAsService: false } linked
                    && state.Components.Existing is { } current && linked.InstallationId == current.InstallationId)
                {
                    await WebClientStartup.RegisterAsync(current, cancellationToken).ConfigureAwait(false);
                }
                report.AppendLine(target.ClusterId + " / " + target.PeerId + Environment.NewLine + Outcome(state, result.Outcome) + Environment.NewLine);
                details.AppendLine(target.Label + Environment.NewLine + Format(result) + Environment.NewLine);
                progress.Report(new("outcome-" + result.Outcome.ToString(), ++completed, targets.Length, target.DisplayName(state.Japanese)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                report.AppendLine(target.DisplayName(state.Japanese) + " · " + Outcome(state, EOutcome.Unknown));
                details.AppendLine(exception.Message);
            }
        }
        if (state.Operation == EInstallerOperation.Uninstall && state.Components.RemoveWebClient)
        {
            WebClientInstallation client = state.Components.Existing!;
            try
            {
                SetupResult result = await InstallerOperations.RemoveClientAsync(new(Guid.NewGuid(), client, state.KeepConfiguration), Path.Combine(state.Package, "updater"),
                    value => progress.Report(value with { Target = state.Text("Web client", "Web クライアント") }), cancellationToken).ConfigureAwait(false);
                report.AppendLine(state.Text("Web client: ", "Web クライアント：") + Outcome(state, result.Outcome));
                details.AppendLine(Format(result));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                report.AppendLine(state.Text("Web client: ", "Web クライアント：") + Outcome(state, EOutcome.Unknown));
                details.AppendLine(exception.Message);
            }
        }
        return new(report.ToString(), details.ToString());
    }

    /// <summary>Webクライアントは個別に更新し、依存するCoreの成功だけを引き継ぎます。</summary>
    /// <returns>このPCで更新できたクライアントです。</returns>
    private static async Task<WebClientInstallation?> UpdateClientsAsync(InstallationViewModel state, Dictionary<ObservedTarget, UpdatePeerResult> cores,
        StringBuilder report, StringBuilder details, IProgress<OperationProgress> progress, CancellationToken cancellationToken = default)
    {
        WebClientInstallation? local = null;
        foreach (ObservedWebClient selected in state.SelectedWebClients)
        {
            ObservedWebClient target = selected;
            string label = target.DisplayName(state.Japanese);
            try
            {
                if (target.Core is { } core && state.Selected.Contains(core))
                {
                    if (!cores.TryGetValue(core, out UpdatePeerResult? updated) || updated.Outcome != EOutcome.Succeeded || updated.InstanceId is not { } instance)
                    {
                        report.AppendLine(label + Environment.NewLine + Outcome(state, EOutcome.NotExecuted));
                        details.AppendLine(state.Text("The associated Core update was not confirmed. The web client was not changed.", "関連するCoreServiceの更新を確認できなかったため、Webクライアントは更新していません。"));
                        continue;
                    }
                    target = target with { Core = core with { Peer = core.Peer! with { InstanceId = instance } } };
                }
                void Report(OperationProgress value) => progress.Report(value with { Target = label });
                SetupResult result;
                if (target.Core is null)
                {
                    result = await InstallerClientUpdates.LocalAsync(target.Installation, Path.Combine(state.Package, "client"), state.Networks.Single(), Report, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await InstallerClientUpdates.RemoteAsync(target, Path.Combine(state.Package, "client"), Report, cancellationToken).ConfigureAwait(false);
                }
                report.AppendLine(label + Environment.NewLine + Outcome(state, result.Outcome));
                details.AppendLine(label + Environment.NewLine + Format(result));
                if (target.Core is null && result.Outcome == EOutcome.Succeeded)
                {
                    local = result.WebClient;
                }
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                report.AppendLine(label + Environment.NewLine + Outcome(state, EOutcome.Unknown));
                details.AppendLine(exception.Message);
            }
        }
        return local;
    }

    /// <summary>一方だけ完了した場合も、確認済みのクライアントとサービスの結果を区別します。</summary>
    /// <returns>完了画面に表示する実際の導入状態です。</returns>
    internal static WizardResult InstallResult(InstallationViewModel state, SetupResult result, WebClientInstallation? readyClient)
    {
        state.Installed = result.Outcome == EOutcome.Succeeded;
        if (state.Installed)
        {
            return new(Outcome(state, result.Outcome), Format(result), result.WebClient);
        }
        if (readyClient is not null)
        {
            string summary = state.Text("The web client is ready.", "Web クライアントの準備は完了しました。")
                + Environment.NewLine + state.Text("Service: ", "サービス：") + Outcome(state, result.Outcome);
            return new(summary, Format(result), readyClient);
        }
        return new(Outcome(state, result.Outcome), Format(result));
    }

    /// <summary>クライアントの配置や起動登録より先に、Core の導入先を確認します。</summary>
    private static void ValidateDestination(InstallationViewModel state, InstallationSettings settings)
    {
        if (!InstallationPolicy.CanInstallInto(settings.Root))
        {
            throw new IOException(state.Text("The installation folder contains existing files. Choose another folder, or use Repair for an existing installation.",
                "インストール先に既存のファイルがあります。別のフォルダーを指定するか、導入済みの Clutch-CS を修復する場合は「修復」を選んでください。"));
        }
    }

    /// <summary>完了、失敗、未実行、結果不明を選択言語で区別します。</summary>
    /// <returns>次の確認につながる結果の文言です。</returns>
    internal static string Outcome(InstallationViewModel state, EOutcome outcome) => outcome switch
    {
        EOutcome.Succeeded => state.Text("Completed.", "完了しました。"),
        EOutcome.Failed => state.Text("Could not complete. Check the details before trying again.", "完了できませんでした。詳細を確認してから、もう一度お試しください。"),
        EOutcome.NotExecuted => state.Text("Not started. Check the details.", "実行されませんでした。詳細を確認してください。"),
        EOutcome.Cancelled => state.Text("Cancelled.", "キャンセルしました。"),
        _ => state.Text("The result could not be confirmed. Check the installation before trying again.", "結果を確認できませんでした。再実行する前に、対象の状態を確認してください。"),
    };

    /// <summary>段階・結果・操作IDを揃えて表示します。</summary>
    /// <returns>保存記録へ辿れる結果です。</returns>
    private static string Format(SetupResult result) => result.Outcome + " · " + result.Stage + Environment.NewLine + result.Detail + Environment.NewLine + result.Root + Environment.NewLine + "Operation: " + result.OperationId;
}
