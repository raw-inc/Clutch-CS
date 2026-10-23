using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>新たな実行先になる前に、同期済み設定をローカル実行へ接続します。</summary>
internal sealed class ServiceReadiness(ServiceRuntime runtime) : IDisposable
{
    private readonly SemaphoreSlim preparation = new(1, 1);
    private string? initialFailure;

    /// <summary>終了状態と初期設定の確認前に新たな実行を許可しません。</summary>
    /// <returns>実行を保留する理由です。</returns>
    internal string? MaintenanceReason(bool stopping)
    {
        if (stopping)
        {
            return "サービスの終了処理中です。";
        }
        if (runtime.Maintenance.IsClosed || !runtime.ManualSafety.Loaded || runtime.Operational.Capture() is null)
        {
            return runtime.Maintenance.State?.Reason ?? "起動時の保留状態を確認しています。";
        }
        return null;
    }

    /// <summary>初期設定の確認失敗を記録し、通常巡回の失敗では確認済みの有効版を保持します。</summary>
    /// <returns>初期化の確認結果です。</returns>
    internal async Task<ConfigurationPhaseResult> InitializeOperationalAsync(bool allowSeed, bool reconcileOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            ConfigurationPhaseResult result;
            if (reconcileOnly)
            {
                result = await runtime.Operational.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ClusterOperationalSettings seed = new(runtime.Configuration.TimeZone.Id, runtime.Configuration.Files.ConflictPolicy,
                    runtime.Configuration.Files.InternalFailurePolicy, runtime.Configuration.HostGroups, runtime.Configuration.SafetyConfirmation,
                    Infrastructure.Observability.Logging.LogRetentionCoordinator.FromOptions(runtime.Configuration.Logs));
                result = await runtime.Operational.InitializeAsync(seed, allowSeed, cancellationToken).ConfigureAwait(false);
            }
            if (allowSeed && result.Outcome != EOutcome.Succeeded && runtime.Operational.Capture() is null)
            {
                RecordInitialFailure(result.Reason);
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            if (allowSeed && runtime.Operational.Capture() is null)
            {
                RecordInitialFailure(null);
            }
            return new(EOutcome.Unknown, "Preparation.InitialOperationalUnconfirmed");
        }
    }

    /// <summary>例外本文を公開せず初回未確認だけを参加条件へ残します。</summary>
    private void RecordInitialFailure(string? reason)
    {
        string failure = reason ?? "Preparation.InitialOperationalUnconfirmed";
        Volatile.Write(ref initialFailure, failure);
        runtime.StartupProblem = failure;
    }

    /// <summary>参加準備または個別定義の利用不能理由です。</summary>
    internal string? Problem
    {
        get; private set;
    }

    /// <summary>通常運用中の診断失敗を全機器停止へ拡大せず、参加時の設定を確認します。</summary>
    /// <returns>現在のローカル実体が通常参加へ進めるかです。</returns>
    internal async ValueTask<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!runtime.ManagementStarted)
        {
            return false;
        }
        await preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await runtime.Conflicts.RefreshAsync(cancellationToken).ConfigureAwait(false);
            await runtime.ManualSafety.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!runtime.ManualSafety.Loaded)
            {
                Problem = "Preparation.SafetyUnconfirmed";
                return false;
            }
            if (runtime.Cluster.IsDetached || (PeerStatusRules.CanParticipate(runtime.Status) && (runtime.Operational.Capture() is not null || Volatile.Read(ref initialFailure) is null)))
            {
                return true;
            }
            LocalPreparationResult common = await runtime.Operational.PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
            if (common.State == ELocalPreparationState.Empty && Volatile.Read(ref initialFailure) is null && runtime.Configuration.Cluster.NewCluster
                && !await ServiceBootstrap.IsInitializedAsync(runtime, cancellationToken).ConfigureAwait(false)
                && !runtime.Cluster.GetPeers().Any(peer => peer.InstanceId != runtime.Cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
            {
                Problem = null;
                return true;
            }
            if (common.State != ELocalPreparationState.Prepared)
            {
                Problem = Volatile.Read(ref initialFailure) ?? common.Reason ?? "Preparation.OperationalUnconfirmed";
                return false;
            }
            LocalPreparationResult workflows = await runtime.Workflows.PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
            await runtime.ManualSafety.RefreshAsync(cancellationToken).ConfigureAwait(false);
            await runtime.Trace.RefreshAsync(cancellationToken).ConfigureAwait(false);
            Problem = workflows.Reason;
            if (workflows.Issues.Count != 0)
            {
                Problem = "Preparation.WorkflowIssues: " + workflows.Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return workflows.State != ELocalPreparationState.Unavailable && runtime.ManualSafety.Loaded;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            Problem = "Preparation.StateUnconfirmed";
            return false;
        }
        finally
        {
            preparation.Release();
        }
    }

    /// <summary>参加確認の終了後にローカル排他を解放します。</summary>
    public void Dispose() => preparation.Dispose();
}
