using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>実プロセス停止・実リリース入替・実UDP 稼働確認を確認します。</summary>
public sealed class LocalUpdateTests
{
    /// <summary>新リリースの新実体がreadyになるまで成功にしません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task AppliesVerifiedReleaseAndDoesNotRepeatCompletedOperation()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", true).ConfigureAwait(true);
        LocalUpdateJob job = scope.Job(manifest, payload, instance, process);
        UpdatePeerResult result = await LocalUpdateExecutor.ExecuteAsync(job).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result) + " directories=" + string.Join(",", Directory.GetDirectories(scope.Root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(scope.Root, path))));
        JsonElement health = await scope.HealthAsync().ConfigureAwait(true);
        Assert.Equal(EUpdatePhase.Completed, result.Phase);
        Assert.Equal("v2", result.ReleaseId);
        Assert.NotEqual(instance, result.InstanceId);
        Assert.Equal("v2", health.GetProperty("buildVersion").GetString());
        Assert.True(Directory.Exists(result.BackupPath));
        UpdatePeerResult repeated = await LocalUpdateExecutor.ExecuteAsync(job).ConfigureAwait(true);
        Assert.Equal(result, repeated);
        Assert.Equal(result.InstanceId, (await scope.HealthAsync().ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
    }

    /// <summary>ペイロード破損は停止前に拒否して旧プロセスを維持します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task CorruptPayloadDoesNotStopOldInstance()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", true).ConfigureAwait(true);
        await File.AppendAllTextAsync(Path.Combine(payload, "behavior.json"), "broken").ConfigureAwait(true);
        UpdatePeerResult result = await LocalUpdateExecutor.ExecuteAsync(scope.Job(manifest, payload, instance, process)).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(instance, (await scope.HealthAsync().ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        Assert.Equal("v1", (await LocalUpdateExecutor.ReadManifestAsync(scope.Options.InstallationRoot!).ConfigureAwait(true)).ReleaseId);
    }

    /// <summary>新リリースがreadyにならなければ実旧版を再起動して検証します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task HealthFailureRestoresAndActuallyRestartsOldRelease()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", false).ConfigureAwait(true);
        UpdatePeerResult result = await LocalUpdateExecutor.ExecuteAsync(scope.Job(manifest, payload, instance, process)).ConfigureAwait(true);
        Assert.True(result.Outcome == EOutcome.Failed, JsonSerializer.Serialize(result));
        JsonElement health = await scope.HealthAsync().ConfigureAwait(true);
        Assert.Equal("update.failedOldReleaseRestored", result.Reason);
        Assert.Equal("v1", result.ReleaseId);
        Assert.Equal("v1", health.GetProperty("buildVersion").GetString());
        Assert.NotEqual(instance, health.GetProperty("instanceId").GetGuid());
    }

    /// <summary>確認期限後の旧版復帰を、実ファイルと指定実体が一致した場合だけ確定します。</summary>
    /// <returns>副作用を再実行しない再確認と、不一致時の元記録保持です。</returns>
    [Fact]
    public async Task LateRestorationConfirmationPreservesTheProcessAndRejectsMismatches()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", false).ConfigureAwait(true);
        LocalUpdateJob job = scope.Job(manifest, payload, instance, process);
        UpdatePeerResult restored = await LocalUpdateExecutor.ExecuteAsync(job).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, restored.Outcome);
        Guid current = restored.InstanceId!.Value;
        UpdateJournal journal = new(new UpdateLayout(job.Options, job.OperationId), job.PeerId);
        UpdatePeerResult unconfirmed = new(job.OperationId, job.PeerId, EUpdatePhase.Interrupted, EOutcome.Unknown, Reason: "update.rollbackHealthUnconfirmed");
        await journal.WriteAsync(job, unconfirmed).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateRestorationConfirmation.ConfirmAsync(job, "v2", "v2", current));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateRestorationConfirmation.ConfirmAsync(job with { PreviousInstanceId = Guid.NewGuid() }, "v1", "v1", current));
        Assert.Equal(unconfirmed, await UpdateRestorationConfirmation.ConfirmAsync(job, "v1", "v1", Guid.NewGuid()).ConfigureAwait(true));
        Assert.Equal(unconfirmed, (await journal.ReadAsync().ConfigureAwait(true))!.Result);
        string installedFile = Path.Combine(scope.Options.InstallationRoot!, "behavior.json");
        byte[] original = await File.ReadAllBytesAsync(installedFile).ConfigureAwait(true);
        await File.AppendAllTextAsync(installedFile, "corrupt").ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateRestorationConfirmation.ConfirmAsync(job, "v1", "v1", current));
        await File.WriteAllBytesAsync(installedFile, original).ConfigureAwait(true);
        UpdatePeerResult confirmed = await UpdateRestorationConfirmation.ConfirmAsync(job, "v1", "v1", current).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, confirmed.Outcome);
        Assert.Equal("update.failedOldReleaseRestored", confirmed.Reason);
        Assert.Equal(current, confirmed.InstanceId);
        Assert.Equal(confirmed, await UpdateRestorationConfirmation.ConfirmAsync(job, "v1", "v1", current).ConfigureAwait(true));
        Assert.Equal(current, (await scope.HealthAsync().ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
    }

    /// <summary>中断済み操作履歴から副作用を再開しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task InterruptedJournalNeverAutomaticallyReapplies()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", true).ConfigureAwait(true);
        LocalUpdateJob job = scope.Job(manifest, payload, instance, process);
        UpdateJournal journal = new(new UpdateLayout(job.Options, job.OperationId), job.PeerId);
        await journal.WriteAsync(job, new UpdatePeerResult(job.OperationId, job.PeerId, EUpdatePhase.Stopped, EOutcome.Unknown)).ConfigureAwait(true);
        UpdatePeerResult result = await LocalUpdateExecutor.ExecuteAsync(job).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("update.manualRecoveryRequired", result.Reason);
        Assert.Equal(instance, (await scope.HealthAsync().ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
    }

    /// <summary>単独実行入口を実子プロセスで実行して操作履歴と稼働確認を確認します。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task StandaloneUpdaterCompletesActualReplacement()
    {
        using UpdateTestScope scope = new();
        (ReleaseManifest oldManifest, string oldPayload) = await scope.PackageAsync("v1", true).ConfigureAwait(true);
        (Guid instance, Contracts.Processes.ProcessIdentity process) = await scope.StartOriginalAsync(oldManifest, oldPayload).ConfigureAwait(true);
        (ReleaseManifest manifest, string payload) = await scope.PackageAsync("v2", true).ConfigureAwait(true);
        LocalUpdateJob job = scope.Job(manifest, payload, instance, process);
        string input = Path.Combine(scope.Root, "job.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(job, ContractJson.Options)).ConfigureAwait(true);
        Assert.True(File.Exists(scope.UpdaterAssembly));
        ProcessStartInfo start = new(scope.DotnetPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(scope.UpdaterAssembly);
        start.ArgumentList.Add("--job");
        start.ArgumentList.Add(input);
        using Process updater = new() { StartInfo = start };
        Assert.True(updater.Start());
        Task<string> stdout = updater.StandardOutput.ReadToEndAsync();
        Task<string> stderr = updater.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await updater.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        JsonElement health = await scope.HealthAsync().ConfigureAwait(true);
        Assert.Equal("v2", health.GetProperty("buildVersion").GetString());
        await scope.StopAsync().ConfigureAwait(true);
        Assert.True(updater.ExitCode == 0, await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true));
        Assert.Contains("completed", await stdout.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true), StringComparison.Ordinal);
        LocalUpdateRecord? record = await new UpdateJournal(new UpdateLayout(job.Options, job.OperationId), job.PeerId).ReadAsync();
        Assert.Equal(EUpdatePhase.Completed, record!.Result.Phase);
    }

    /// <summary>portable相対パスを越えるリリース項目を拒否します。</summary>
    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    [InlineData("nested/../../outside")]
    [InlineData("nested\\file")]
    [InlineData("CON.txt")]
    public void RejectsTraversalAndDevicePaths(string path)
    {
        Assert.False(ReleaseVerifier.IsRelativePath(path));
    }
}
