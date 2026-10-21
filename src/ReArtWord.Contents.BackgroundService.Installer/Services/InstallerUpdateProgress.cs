using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>更新処理の呼び出しと確認済み結果を、画面の進捗へ反映します。</summary>
internal sealed class InstallerUpdateProgress(IUpdatePeerTransport inner, IReadOnlyList<string> peers, Action<OperationProgress>? report) : IUpdatePeerTransport
{
    private readonly Dictionary<string, int> completed = peers.ToDictionary(peer => peer, _ => 0, StringComparer.Ordinal);
    private DateTimeOffset nextTransfer;

    /// <summary>更新で確認する段階です。</summary>
    private enum EStep
    {
        Check,
        Transfer,
        Stop,
        Backup,
        Install,
        Ready,
        Release,
    }

    /// <summary>現在の実環境を確認します。</summary>
    /// <returns>対象のRIDです。</returns>
    public async Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-check", peerId);
        string result = await inner.GetPlatformAsync(peerId, cancellationToken).ConfigureAwait(false);
        Complete(peerId, EStep.Check);
        return result;
    }

    /// <summary>受信側の検証完了までを転送工程として表示します。</summary>
    /// <returns>実際の準備結果です。</returns>
    public async Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-transfer", peerId);
        UpdatePeerResult result = await inner.StageAsync(request, peerId, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded)
        {
            Complete(peerId, EStep.Transfer);
        }
        Observe(result);
        return result;
    }

    /// <summary>更新開始の要求を一度だけ委譲します。</summary>
    /// <returns>受理または実結果です。</returns>
    public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-apply", peerId);
        UpdatePeerResult result = await inner.DispatchAsync(operationId, peerId, cancellationToken).ConfigureAwait(false);
        Observe(result);
        return result;
    }

    /// <summary>保存された工程を画面へ渡します。</summary>
    /// <returns>照会で確認した結果です。</returns>
    public async Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        UpdatePeerResult result = await inner.ReadAsync(operationId, peerId, cancellationToken).ConfigureAwait(false);
        Observe(result);
        return result;
    }

    /// <summary>切り戻しの要求を一度だけ委譲します。</summary>
    /// <returns>復元の受理または結果です。</returns>
    public async Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-restoring", peerId);
        UpdatePeerResult result = await inner.RollbackAsync(operationId, peerId, cancellationToken).ConfigureAwait(false);
        Observe(result);
        return result;
    }

    /// <summary>復帰を確認できない対象の除外を委譲します。</summary>
    /// <returns>除外確認の完了です。</returns>
    public Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-exclude", peerId);
        return inner.ExcludeAsync(peerId, cancellationToken);
    }

    /// <summary>通常処理の停止を委譲します。</summary>
    /// <returns>停止確認の完了です。</returns>
    public Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default)
    {
        Report("update-hold", null);
        return inner.StopClusterAsync(operationId, peerIds, cancellationToken);
    }

    /// <summary>通常処理への復帰を確認して工程を完了します。</summary>
    /// <returns>復帰確認の完了です。</returns>
    public async Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
    {
        Report("update-finish", peerId);
        await inner.ReleaseAsync(operationId, peerId, cancellationToken).ConfigureAwait(false);
        Complete(peerId, EStep.Release);
        Report("update-Completed", peerId);
    }

    /// <summary>大量転送でも画面更新を一定間隔に抑え、最後の値は必ず通知します。</summary>
    internal void Transfer(string peer, long bytes, long total)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < nextTransfer && bytes != total)
        {
            return;
        }
        nextTransfer = now + InstallationDefaults.PollInterval;
        report?.Invoke(new("update-transfer", completed.Values.Sum(), TotalSteps(), peer, bytes, total));
    }

    /// <summary>実行先が確認した工程だけを完了として数えます。</summary>
    private void Observe(UpdatePeerResult result)
    {
        EStep? step = result.Phase switch
        {
            EUpdatePhase.Stopped => EStep.Stop,
            EUpdatePhase.BackedUp => EStep.Backup,
            EUpdatePhase.Installed or EUpdatePhase.Starting => EStep.Install,
            _ => null,
        };
        if (step.HasValue)
        {
            Complete(result.PeerId, step.Value);
        }
        if (result.Outcome == EOutcome.Succeeded && result.Phase is EUpdatePhase.Completed or EUpdatePhase.RolledBack)
        {
            Complete(result.PeerId, EStep.Ready);
        }
        Report("update-" + result.Phase, result.PeerId);
    }

    /// <summary>再照会で同じ工程を重複して数えません。</summary>
    private void Complete(string peer, EStep step) => completed[peer] = Math.Max(completed[peer], (int)step + 1);

    /// <summary>選択した全対象の工程数です。</summary>
    /// <returns>固定した工程数です。</returns>
    private int TotalSteps() => completed.Count * Enum.GetValues<EStep>().Length;

    /// <summary>確認済み工程数と現在の作業を通知します。</summary>
    private void Report(string stage, string? peer) => report?.Invoke(new(stage, completed.Values.Sum(), TotalSteps(), peer));
}
