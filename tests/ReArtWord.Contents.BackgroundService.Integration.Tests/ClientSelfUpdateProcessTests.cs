using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実ClientHostと独立Updaterで再起動・復元・重複拒否の境界を確認します。</summary>
public sealed class ClientSelfUpdateProcessTests
{
    /// <summary>成功と新版の健康確認失敗を、実プロセス・固定パス・保存結果で区別します。</summary>
    /// <returns>実HTTP接続による入替または復帰確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentUpdaterPreservesPathAndDoesNotReplayAsync(bool failHealth)
    {
        ClientUpdateInstallation scope = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = scope.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await scope.InitializeAsync(timeout.Token).ConfigureAwait(true);
        ClientUpdateHealth before = await scope.HealthAsync(timeout.Token).ConfigureAwait(true);
        ReleaseManifest release = scope.Manifest;
        if (failHealth)
        {
            release = release with { ExpectedBuildVersion = "unavailable-test-version" };
        }
        ClientUpdateJob job = await scope.PrepareAsync(release, 5000, timeout.Token).ConfigureAwait(true);
        Process updater = scope.StartUpdater(job);
        await CorePeer.WaitAsync(async token => string.Equals((await ClientUpdateFiles.ReadAsync(scope.State, job.OperationId, token).ConfigureAwait(true))?.State,
            "WaitingForExit", StringComparison.Ordinal), timeout.Token).ConfigureAwait(true);
        await scope.StopOriginalAsync(timeout.Token).ConfigureAwait(true);
        await ProcessCompletion.WaitAsync(updater, timeout.Token).ConfigureAwait(true);
        ClientUpdateProgress result = (await ClientUpdateFiles.ReadAsync(scope.State, job.OperationId, timeout.Token).ConfigureAwait(true))!;
        EOutcome expected = EOutcome.Succeeded;
        string releaseId = "new";
        if (failHealth)
        {
            expected = EOutcome.Failed;
            releaseId = "old";
            Assert.Equal("RolledBack", result.State);
        }
        Assert.True(expected == result.Outcome, JsonSerializer.Serialize(result, ContractJson.Options));
        ClientUpdateHealth after = await scope.HealthAsync(timeout.Token).ConfigureAwait(true);
        Assert.NotEqual(before.Process, after.Process);
        Assert.Equal(releaseId, (await LocalUpdateExecutor.ReadManifestAsync(scope.Installed, timeout.Token).ConfigureAwait(true)).ReleaseId);
        Process repeated = scope.StartUpdater(job);
        await ProcessCompletion.WaitAsync(repeated, timeout.Token).ConfigureAwait(true);
        Assert.Equal(result, await ClientUpdateFiles.ReadAsync(scope.State, job.OperationId, timeout.Token).ConfigureAwait(true));
        Assert.Equal(after.Process, (await scope.HealthAsync(timeout.Token).ConfigureAwait(true)).Process);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClientUpdateExecutor.ExecuteAsync(job with { TimeoutMilliseconds = 6000 }, timeout.Token));
    }

    /// <summary>破損候補と終了しない元ClientHostは停止・ファイル入替へ進みません。</summary>
    /// <returns>元実体と旧版を維持する確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrStillRunningOriginalIsNotReplacedAsync(bool corrupt)
    {
        ClientUpdateInstallation scope = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = scope.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await scope.InitializeAsync(timeout.Token).ConfigureAwait(true);
        ClientUpdateHealth before = await scope.HealthAsync(timeout.Token).ConfigureAwait(true);
        ClientUpdateJob job = await scope.PrepareAsync(scope.Manifest, 1000, timeout.Token).ConfigureAwait(true);
        if (corrupt)
        {
            await File.AppendAllTextAsync(Path.Combine(new ClientUpdateFiles(job).Candidate, job.Manifest.EntryPoint), "corrupt", timeout.Token).ConfigureAwait(true);
        }
        Process updater = scope.StartUpdater(job);
        await ProcessCompletion.WaitAsync(updater, timeout.Token).ConfigureAwait(true);
        ClientUpdateProgress result = (await ClientUpdateFiles.ReadAsync(scope.State, job.OperationId, timeout.Token).ConfigureAwait(true))!;
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(before.Process, (await scope.HealthAsync(timeout.Token).ConfigureAwait(true)).Process);
        Assert.Equal("old", (await LocalUpdateExecutor.ReadManifestAsync(scope.Installed, timeout.Token).ConfigureAwait(true)).ReleaseId);
        Assert.False(Directory.Exists(new ClientUpdateFiles(job).Backup));
    }

    /// <summary>全体更新APIでCore復帰後にClientHostが自己終了し、再接続後も同じ結果を返します。</summary>
    /// <returns>実UDP・HTTP・複数プロセスと再起動履歴の確認です。</returns>
    [Fact]
    public async Task FleetUpdatesCoreThenClientAndRestoresItsHistoryAsync()
    {
        ClientUpdateInstallation scope = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = scope.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await scope.InitializeAsync(timeout.Token).ConfigureAwait(true);
        ClientUpdateHealth original = await scope.HealthAsync(timeout.Token).ConfigureAwait(true);
        string corePayload = Path.Combine(scope.Root, "core");
        ReleaseManifest previous = await RollingUpdatePeer.PackageAsync(corePayload, timeout.Token).ConfigureAwait(true);
        RollingUpdatePeer peer = new(scope.Root, Guid.NewGuid().ToString("N"), "only-host", CorePeer.AvailablePort(), corePayload, previous, true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        Guid before = await peer.StartAsync(timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage configuration = await scope.Http.PutAsJsonAsync(new Uri("client/networks", UriKind.Relative), new[] { peer.Binding }, ContractJson.Options, timeout.Token).ConfigureAwait(true))
        {
            configuration.EnsureSuccessStatusCode();
        }
        ReleaseManifest next = previous with { ReleaseId = "new", ExpectedBuildVersion = scope.Manifest.ExpectedBuildVersion };
        FleetReleaseSelection selection = new(next, scope.Root, true, PeerTimeoutMilliseconds: 30000,
            Variants: [new(next, "core")], Client: new(scope.Manifest, "client"));
        FleetUpdateRequest? plan = null;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await scope.Http.PostAsJsonAsync(new Uri("client/updates/preview", UriKind.Relative), selection, ContractJson.Options, token).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            plan = await response.Content.ReadFromJsonAsync<FleetUpdateRequest>(ContractJson.Options, token).ConfigureAwait(true);
            return plan?.Targets.Count == 1;
        }, timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage accepted = await scope.Http.PostAsJsonAsync(new Uri("client/updates", UriKind.Relative), plan, ContractJson.Options, timeout.Token).ConfigureAwait(true))
        {
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(true));
        }
        FleetUpdateStatus? status = null;
        int requestTimeouts = 0;
        try
        {
            await CorePeer.WaitAsync(async token =>
            {
                try
                {
                    status = await scope.ReadAsync<FleetUpdateStatus>("client/updates/" + plan!.OperationId.ToString("D"), token).ConfigureAwait(true);
                    return status?.Completed == true;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    requestTimeouts++;
                    return false;
                }
            }, Timeout.InfiniteTimeSpan, timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 更新の完了待ちは試験全体の期限を使い、期限切れ後の診断だけを別の短い期限で取得します。
            using CancellationTokenSource diagnosis = new(TimeSpan.FromSeconds(5));
            FleetUpdateStatus? saved = await UpdateProgressFile.ReadAsync<FleetUpdateStatus>(
                Path.Combine(scope.State, "fleet-updates", plan!.OperationId.ToString("N") + ".json"), diagnosis.Token).ConfigureAwait(true);
            ClientUpdateProgress? client = await ClientUpdateFiles.ReadAsync(scope.State, plan.OperationId, diagnosis.Token).ConfigureAwait(true);
            Assert.Fail(JsonSerializer.Serialize(new
            {
                Reason = "Fleet update did not complete within the test deadline.",
                RequestTimeouts = requestTimeouts,
                Observed = status is null ? null
                                          : FleetUpdateView.From(status),
                Saved = saved is null ? null
                                      : FleetUpdateView.From(saved),
                LocalClient = client
            }, ContractJson.Options));
        }
        Assert.True(status!.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(status, ContractJson.Options));
        Assert.Equal(EOutcome.Succeeded, status.Client!.Outcome);
        Assert.Single(Assert.Single(status.Peers).Attempts);
        Assert.NotEqual(before, (await peer.HealthAsync(timeout.Token).ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        Assert.NotEqual(original.Process, (await scope.HealthAsync(timeout.Token).ConfigureAwait(true)).Process);
        using HttpResponseMessage repeated = await scope.Http.PostAsJsonAsync(new Uri("client/updates", UriKind.Relative), plan, ContractJson.Options, timeout.Token).ConfigureAwait(true);
        repeated.EnsureSuccessStatusCode();
        Assert.Equal(plan!.OperationId, (await repeated.Content.ReadFromJsonAsync<FleetUpdateStatus>(ContractJson.Options, timeout.Token).ConfigureAwait(true))!.Request.OperationId);
    }
}
