using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>開始の競合や不完全な記録を、実行者以外の確定失敗へ変えないことを検証します。</summary>
public sealed class SetupOperationOwnershipTests
{
    /// <summary>同じ入力を同時に受け付けても、開始権は一つで、他は結果照会になります。</summary>
    /// <returns>原子的な開始記録と未確定結果の確認です。</returns>
    [Fact]
    public async Task ConcurrentClaimsHaveOnlyOneOwnerAsync()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string directory = Path.Combine(fixture.Root, "concurrent");
        Directory.CreateDirectory(directory);
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings);
        SetupOperationJournal[] journals = Enumerable.Range(0, 16).Select(_ => new SetupOperationJournal(job.OperationId, directory, job.Settings.Root)).ToArray();
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SetupResult?>[] attempts = journals.Select(async journal =>
        {
            await ready.Task.ConfigureAwait(false);
            return await journal.BeginAsync(job).ConfigureAwait(false);
        }).ToArray();
        ready.SetResult();
        SetupResult?[] results = await Task.WhenAll(attempts);
        Assert.Single(results, result => result is null);
        Assert.All(results.Where(result => result is not null), result =>
        {
            Assert.Equal(EOutcome.Unknown, result!.Outcome);
            Assert.False(result.Finished);
        });
        Assert.False(File.Exists(Path.Combine(directory, "result.json")));
        Assert.Equal(ContractJson.Hash(job), await File.ReadAllTextAsync(Path.Combine(directory, "started")));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        int owner = Array.FindIndex(results, result => result is null);
        SetupResult completed = new(job.OperationId, EOutcome.Succeeded, "ready", "Completed by the owner.", job.Settings.Root);
        await journals[owner].CompleteAsync(completed);
        foreach (SetupOperationJournal journal in journals)
        {
            Assert.Equal(completed, await journal.BeginAsync(job));
        }
    }

    /// <summary>開始権のない実行者は進捗や結果に触れず、所有者の完了後も同じ結果を返します。</summary>
    /// <returns>Core導入、Webクライアント導入・削除・更新の確認です。</returns>
    [Theory]
    [InlineData("core")]
    [InlineData("client-setup")]
    [InlineData("client-remove")]
    [InlineData("client-update")]
    public async Task UnclaimedExecutorsDoNotWriteProgressOrResultsAsync(string kind)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string directory = Path.Combine(fixture.Root, "owned");
        Directory.CreateDirectory(directory);
        Guid operation = Guid.NewGuid();
        WebClientInstallation client = new(Guid.NewGuid(), Path.Combine(fixture.Root, "client"), "Clutch-CS.ClientHost.exe", 55001, true);
        SetupJob core = new(operation, ESetupAction.Install, fixture.Settings);
        WebClientSetupJob setup = new(operation, fixture.Settings);
        WebClientRemovalJob removal = new(operation, client, true);
        ReleaseManifest manifest = new("fixture-v2", "win-x64", client.EntryPoint, [], [], "fixture-v2");
        WebClientUpdateJob update = new(operation, client, new(1, DateTimeOffset.UnixEpoch), manifest, fixture.Root);
        object job = kind switch { "core" => core, "client-setup" => setup, "client-remove" => removal, _ => update };
        Func<Task<SetupResult>> execute = kind switch
        {
            "core" => () => new SetupExecutor(fixture.Platform).ExecuteAsync(core, directory),
            "client-setup" => () => new WebClientSetup(fixture.Platform).ExecuteAsync(setup, directory),
            "client-remove" => () => new WebClientRemoval(fixture.Platform).ExecuteAsync(removal, directory),
            _ => () => new WebClientUpdateExecutor(fixture.Platform).ExecuteAsync(update, directory),
        };
        SetupOperationJournal owner = new(operation, directory, fixture.Settings.Root);
        Assert.Null(await owner.BeginAsync(job));
        string progress = Path.Combine(directory, "progress.json");
        await File.WriteAllTextAsync(progress, "owner progress");
        SetupResult pending = await execute();
        Assert.Equal(EOutcome.Unknown, pending.Outcome);
        Assert.False(pending.Finished);
        Assert.False(File.Exists(Path.Combine(directory, "result.json")));
        Assert.Equal("owner progress", await File.ReadAllTextAsync(progress));
        Assert.Empty(fixture.Platform.Calls);
        SetupResult completed = new(operation, EOutcome.Succeeded, "ready", "Owner result.", fixture.Settings.Root);
        await owner.CompleteAsync(completed);
        Assert.Equal(completed, await execute());
        Assert.Equal("owner progress", await File.ReadAllTextAsync(progress));
        Assert.Empty(fixture.Platform.Calls);
    }

    /// <summary>既存記録を確認できない場合は、開始も確定結果の保存もしません。</summary>
    /// <returns>不完全記録を保持した照会結果です。</returns>
    [Theory]
    [InlineData("partial-start")]
    [InlineData("locked-start")]
    [InlineData("partial-result")]
    [InlineData("foreign-result")]
    [InlineData("orphan-result")]
    public async Task UnreadableRecordsRemainUnknownAsync(string problem)
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string directory = Path.Combine(fixture.Root, "unconfirmed");
        Directory.CreateDirectory(directory);
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings);
        string started = Path.Combine(directory, "started"), result = Path.Combine(directory, "result.json");
        SetupOperationJournal journal = new(job.OperationId, directory, fixture.Settings.Root);
        const string orphan = "orphan-result", locked = "locked-start";
        if (!string.Equals(problem, orphan, StringComparison.Ordinal))
        {
            Assert.Null(await journal.BeginAsync(job));
        }
        switch (problem)
        {
            case "partial-start":
                await File.WriteAllTextAsync(started, "incomplete");
                break;
            case "partial-result":
                await File.WriteAllTextAsync(result, "{");
                break;
            case "foreign-result":
            case "orphan-result":
                await InstallationFiles.WriteAsync(result, new SetupResult(Guid.NewGuid(), EOutcome.Failed, "other", "Unrelated result.", fixture.Root));
                break;
        }
        byte[]? before = File.Exists(result) ? await File.ReadAllBytesAsync(result)
                                             : null;
        using FileStream? held = string.Equals(problem, locked, StringComparison.Ordinal) ? new(started, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                                                                                          : null;
        SetupResult? observed = await journal.BeginAsync(job);
        Assert.Equal(EOutcome.Unknown, observed!.Outcome);
        Assert.False(observed.Finished);
        if (before is not null)
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(result));
        }
        else
        {
            Assert.False(File.Exists(result));
        }
    }

    /// <summary>別プロセスのUpdaterにも、所有者が未完了の同一jobを渡します。</summary>
    /// <returns>二つの実プロセスが結果を上書きせず照会で終了する確認です。</returns>
    [Fact]
    public async Task TwoUpdaterProcessesPreserveTheExistingOwnerAsync()
    {
        using SetupFixture fixture = new();
        await fixture.InitializeAsync();
        string directory = Path.Combine(fixture.Root, "processes");
        Directory.CreateDirectory(directory);
        SetupJob job = new(Guid.NewGuid(), ESetupAction.Install, fixture.Settings with { LicenseAccepted = false });
        string path = Path.Combine(directory, "job.json");
        await InstallationFiles.WriteAsync(path, job);
        SetupOperationJournal owner = new(job.OperationId, directory, job.Settings.Root);
        Assert.Null(await owner.BeginAsync(job));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        SetupResult[] results = await Task.WhenAll(SetupOperationProcess.ObserveAsync(path, timeout.Token), SetupOperationProcess.ObserveAsync(path, timeout.Token));
        Assert.All(results, result =>
        {
            Assert.Equal(EOutcome.Unknown, result.Outcome);
            Assert.False(result.Finished);
        });
        Assert.False(File.Exists(Path.Combine(directory, "result.json")));
        Assert.False(Directory.Exists(job.Settings.Root));
        Assert.Equal(ContractJson.Hash(job), await File.ReadAllTextAsync(Path.Combine(directory, "started")));
    }
}
