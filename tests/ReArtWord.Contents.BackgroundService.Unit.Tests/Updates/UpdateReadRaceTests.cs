using System.Runtime.InteropServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>完了記録の保存とUpdater終了の境界で、成功を中断と取り違えないことを検証します。</summary>
public sealed class UpdateReadRaceTests
{
    /// <summary>古い進行記録を読んだ直後に書込元が終了しても、保存済みの最終結果を返します。</summary>
    /// <returns>同じ操作の最終結果です。</returns>
    [Theory]
    [InlineData(EUpdatePhase.Completed, EOutcome.Succeeded)]
    [InlineData(EUpdatePhase.RolledBack, EOutcome.Succeeded)]
    [InlineData(EUpdatePhase.Failed, EOutcome.Failed)]
    public async Task WriterExitAfterTerminalSaveDoesNotBecomeInterruptedAsync(EUpdatePhase phase, EOutcome outcome)
    {
        using UpdateTestScope scope = new();
        (LocalUpdateJob job, UpdateJournal journal, UpdatePeerAgent agent) = await CreateAsync(scope).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using ManualResetEventSlim completed = new(false);
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<UpdatePeerResult> reading = Task.Run(() => agent.ReadObservedAsync(job.OperationId, _ =>
        {
            observed.TrySetResult();
            completed.Wait(timeout.Token);
            return false;
        }, timeout.Token), timeout.Token);
        UpdatePeerResult expected = new(job.OperationId, job.PeerId, phase, outcome, "next", InstanceId: Guid.NewGuid());
        try
        {
            await observed.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            await journal.WriteAsync(job, expected, timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            completed.Set();
        }
        Assert.Equal(expected, await reading.ConfigureAwait(true));
        Assert.Equal(expected, (await journal.ReadAsync(timeout.Token).ConfigureAwait(true))!.Result);
    }

    /// <summary>最終記録を残さず終了したUpdaterは、処理を再開せず結果不明として扱います。</summary>
    /// <returns>本当の中断と、記録を書き換えない読取りの確認です。</returns>
    [Fact]
    public async Task MissingWriterWithoutFinalRecordRemainsInterruptedAsync()
    {
        using UpdateTestScope scope = new();
        (LocalUpdateJob job, UpdateJournal journal, UpdatePeerAgent agent) = await CreateAsync(scope).ConfigureAwait(true);
        UpdatePeerResult result = await agent.ReadObservedAsync(job.OperationId, _ => false).ConfigureAwait(true);
        Assert.Equal(EUpdatePhase.Interrupted, result.Phase);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(EUpdatePhase.Starting, (await journal.ReadAsync().ConfigureAwait(true))!.Result.Phase);
    }

    /// <summary>サービスや実行ファイルを起動せず、実ファイルに進行中の操作を保存します。</summary>
    /// <returns>同じジョブを参照する記録と読取り元です。</returns>
    private static async Task<(LocalUpdateJob Job, UpdateJournal Journal, UpdatePeerAgent Agent)> CreateAsync(UpdateTestScope scope)
    {
        ReleaseManifest manifest = new("next", RuntimeInformation.RuntimeIdentifier, "app", [], [], "next");
        LocalUpdateJob job = scope.Job(manifest, Path.Combine(scope.Options.StagingRoot!, "payload"), Guid.NewGuid(), ProcessOs.CurrentIdentity());
        UpdateLayout layout = new(scope.Options, job.OperationId);
        layout.CreateDirectories();
        await File.WriteAllTextAsync(layout.JobPath, JsonSerializer.Serialize(job, ContractJson.Options)).ConfigureAwait(false);
        UpdateJournal journal = new(layout, job.PeerId);
        await journal.WriteAsync(job, new(job.OperationId, job.PeerId, EUpdatePhase.Starting, EOutcome.Unknown)).ConfigureAwait(false);
        return (job, journal, new(scope.Options, job.PeerId, job.PreviousInstanceId, job.ApplicationArguments));
    }
}
