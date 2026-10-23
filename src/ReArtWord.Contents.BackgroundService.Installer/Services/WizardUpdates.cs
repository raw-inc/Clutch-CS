using System.Text;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Installer.ViewModels;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>選択した更新方式を適用し、CoreとWebクライアントの結果をまとめます。</summary>
internal static class WizardUpdates
{
    /// <summary>処理中の対象の扱いを固定して更新します。</summary>
    /// <returns>確認できた結果と診断です。</returns>
    internal static async Task<WizardResult> ExecuteAsync(InstallationViewModel state, IProgress<OperationProgress> progress, CancellationToken cancellationToken = default)
    {
        ObservedTarget[] targets = state.Selected.ToArray();
        StringBuilder report = new();
        StringBuilder details = new();
        Dictionary<ObservedTarget, UpdatePeerResult> coreResults = [];
        ObservedTarget[][] clusters = Groups(targets, state.UpdateMode);
        if (state.Fleet && targets.Length != 0)
        {
            FleetUpdateStatus result = await InstallerFleetUpdate.ExecuteAsync(targets, Path.Combine(state.Package, "core"), progress, cancellationToken).ConfigureAwait(false);
            report.AppendLine(WizardExecution.Outcome(state, result.Outcome));
            details.AppendLine("Operation: " + result.Request.OperationId + Environment.NewLine + result.Reason);
            foreach (FleetUpdatePeerProgress peer in result.Peers)
            {
                EOutcome peerOutcome = peer.State switch
                {
                    "Completed" => EOutcome.Succeeded,
                    "Failed" => EOutcome.Failed,
                    "Unconfirmed" => EOutcome.Unknown,
                    _ => EOutcome.NotExecuted,
                };
                report.AppendLine(peer.Target.ClusterId + " / " + peer.Target.Peer.PeerId + " · " + WizardExecution.Outcome(state, peerOutcome));
                details.AppendLine(peer.State + " · " + peer.Reason);
                ObservedTarget target = targets.Single(target => string.Equals(target.ClusterId, peer.Target.ClusterId, StringComparison.Ordinal)
                    && string.Equals(target.PeerId, peer.Target.Peer.PeerId, StringComparison.Ordinal));
                string completedState = "Completed";
                if (string.Equals(peer.State, completedState, StringComparison.Ordinal) && peer.Attempts.Count != 0 && peer.Attempts[^1].Result is { } confirmed)
                {
                    coreResults[target] = confirmed;
                }
            }
            if (result.Outcome == EOutcome.Unknown)
            {
                return new(report.ToString(), details.ToString());
            }
            clusters = [];
        }
        int index = 0;
        int completedSteps = 0;
        int totalSteps = 0;
        foreach (ObservedTarget[] cluster in clusters)
        {
            index++;
            string group = $"({index} / {clusters.Length}) {cluster[0].ClusterId}";
            int currentSteps = 0;
            EOutcome outcome = EOutcome.Unknown;
            progress.Report(new("update-check", completedSteps, totalSteps, group));
            try
            {
                Contracts.Updates.Cluster.ClusterUpdateStatus result = await InstallerOperations.UpdateAsync(cluster.ToArray(), Path.Combine(state.Package, "core"), state.StopActive,
                    value =>
                    {
                        currentSteps = value.CompletedSteps;
                        totalSteps = value.TotalSteps / cluster.Length * targets.Length;
                        string target = group;
                        if (value.Target is not null)
                        {
                            target += " / " + value.Target;
                        }
                        progress.Report(value with { CompletedSteps = completedSteps + currentSteps, TotalSteps = totalSteps, Target = target });
                    }, cancellationToken).ConfigureAwait(false);
                outcome = result.Outcome;
                report.AppendLine(cluster[0].ClusterId + " · " + WizardExecution.Outcome(state, result.Outcome));
                details.AppendLine(cluster[0].ClusterId + " · " + result.Outcome + " · " + result.Reason);
                foreach (Contracts.Updates.UpdatePeerResult peer in result.Peers)
                {
                    coreResults[cluster.Single(target => string.Equals(target.PeerId, peer.PeerId, StringComparison.Ordinal))] = peer;
                    report.AppendLine("  " + peer.PeerId + " · " + WizardExecution.Outcome(state, peer.Outcome));
                    details.AppendLine("  " + peer.PeerId + " · " + peer.Outcome + " · " + peer.Phase + " · " + peer.Reason);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                report.AppendLine(cluster[0].ClusterId + " · " + WizardExecution.Outcome(state, EOutcome.Unknown));
                details.AppendLine(exception.Message);
            }
            completedSteps += currentSteps;
            progress.Report(new("outcome-" + outcome, completedSteps, totalSteps, group));
            if (outcome == EOutcome.Unknown)
            {
                return new(report.ToString(), details.ToString());
            }
        }
        WebClientInstallation? updatedClient = await WizardExecution.UpdateClientsAsync(state, coreResults, report, details, progress, cancellationToken).ConfigureAwait(false);
        return new(report.ToString(), details.ToString(), updatedClient);
    }

    /// <summary>処理中の対象を除外する場合は、ほかの対象の準備と停止を巻き込みません。</summary>
    /// <returns>一括停止または対象別に分けた更新単位です。</returns>
    internal static ObservedTarget[][] Groups(IReadOnlyList<ObservedTarget> targets, EInstallerUpdateMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (mode == EInstallerUpdateMode.SkipActive)
        {
            return targets.Select(target => new[] { target }).ToArray();
        }
        return targets.GroupBy(target => (target.ClusterId, target.Binding)).Select(group => group.ToArray()).ToArray();
    }
}
