using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>クライアント更新のファイル境界、復帰と一回限りの実行を検証します。</summary>
public sealed class WebClientUpdateTests
{
    /// <summary>公開版と同じ実行ファイル名で更新し、設定・導入ID・起動方法を保持します。</summary>
    /// <returns>更新と二重実行防止の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdatesReleasedLayoutWithoutChangingSettings(bool service)
    {
        using SetupFixture fixture = new();
        WebClientUpdateJob job = await PrepareAsync(fixture, service);
        string configuration = Path.Combine(job.Installation.Root, "config", "retained.json");
        await File.WriteAllTextAsync(configuration, "retained configuration");
        WebClientUpdateExecutor updater = new(fixture.Platform);
        string journal = Path.Combine(fixture.Root, "update");
        SetupResult result = await updater.ExecuteAsync(job, journal);
        Assert.True(result.Outcome == EOutcome.Succeeded, result.Detail);
        WebClientInstallation updated = Assert.IsType<WebClientInstallation>(result.WebClient);
        Assert.Equal(job.Installation with { EntryPoint = job.Manifest.EntryPoint }, updated);
        Assert.Equal(updated, await WebClientCatalog.ReadAsync(updated.Root));
        Assert.Equal("retained configuration", await File.ReadAllTextAsync(configuration));
        Assert.Equal(job.Installation.EntryPoint, job.Manifest.EntryPoint);
        Assert.Equal("new client release", await File.ReadAllTextAsync(Path.Combine(WebClientCatalog.ProgramRoot(updated), job.Manifest.EntryPoint)));
        Assert.Equal(result, await updater.ExecuteAsync(job, journal));
        Assert.Equal(1, fixture.Platform.ClientStops);
        Assert.Single(fixture.Platform.ClientCalls, call => string.Equals(call, "start", StringComparison.Ordinal));
        Assert.Empty(fixture.Platform.Calls);
    }

    /// <summary>新しい版の起動に失敗したら、以前のファイル・記録・起動登録を復元します。</summary>
    /// <returns>復帰後の版と結果の確認です。</returns>
    [Fact]
    public async Task FailedStartupRestoresPreviousClient()
    {
        using SetupFixture fixture = new();
        WebClientUpdateJob job = await PrepareAsync(fixture);
        int starts = 0;
        fixture.Platform.StageBehavior = stage =>
        {
            const string starting = "client-start";
            if (string.Equals(stage, starting, StringComparison.Ordinal) && ++starts == 1)
            {
                throw new IOException("New client did not become ready.");
            }
            return Task.CompletedTask;
        };
        SetupResult result = await new WebClientUpdateExecutor(fixture.Platform).ExecuteAsync(job, Path.Combine(fixture.Root, "update"));
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.Equal("restored", result.Stage);
        Assert.Equal(job.Installation, result.WebClient);
        Assert.Equal(job.Installation, await WebClientCatalog.ReadAsync(job.Installation.Root));
        Assert.Equal("fixture-v1", (await LocalUpdateExecutor.ReadManifestAsync(WebClientCatalog.ProgramRoot(job.Installation))).ExpectedBuildVersion);
        Assert.Equal("fixture:client", await File.ReadAllTextAsync(Path.Combine(WebClientCatalog.ProgramRoot(job.Installation), job.Installation.EntryPoint)));
        Assert.Equal(2, starts);
    }

    /// <summary>配布物が壊れている場合は稼働中のプロセスに触れません。</summary>
    /// <returns>停止前検証の確認です。</returns>
    [Fact]
    public async Task CorruptPayloadDoesNotStopClient()
    {
        using SetupFixture fixture = new();
        WebClientUpdateJob job = await PrepareAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(job.PayloadRoot, job.Manifest.EntryPoint), "corrupt");
        SetupResult result = await new WebClientUpdateExecutor(fixture.Platform).ExecuteAsync(job, Path.Combine(fixture.Root, "update"));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(0, fixture.Platform.ClientStops);
        Assert.Equal(job.Installation, await WebClientCatalog.ReadAsync(job.Installation.Root));
    }

    /// <summary>確認後の再起動を別のプロセスとして扱います。</summary>
    /// <returns>古い確認による停止を拒否した結果です。</returns>
    [Fact]
    public async Task ChangedProcessIsNotStopped()
    {
        using SetupFixture fixture = new();
        WebClientUpdateJob job = await PrepareAsync(fixture);
        fixture.Platform.ClientProcess = fixture.Platform.ClientProcess with { StartedAt = fixture.Platform.ClientProcess.StartedAt.AddSeconds(1) };
        SetupResult result = await new WebClientUpdateExecutor(fixture.Platform).ExecuteAsync(job, Path.Combine(fixture.Root, "update"));
        Assert.Equal(EOutcome.NotExecuted, result.Outcome);
        Assert.Equal(0, fixture.Platform.ClientStops);
    }

    /// <summary>旧版の起動も失敗した場合、復帰済みとは報告しません。</summary>
    /// <returns>未確認を保持した結果です。</returns>
    [Fact]
    public async Task FailedRecoveryRemainsUnknown()
    {
        using SetupFixture fixture = new();
        WebClientUpdateJob job = await PrepareAsync(fixture);
        fixture.Platform.StageBehavior = _ => throw new IOException("Cannot start either version.");
        SetupResult result = await new WebClientUpdateExecutor(fixture.Platform).ExecuteAsync(job, Path.Combine(fixture.Root, "update"));
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("restore", result.Stage);
    }

    /// <summary>v0.1.1-alpha以降と同じ実行ファイル名で、内容と版の異なる更新を用意します。</summary>
    /// <returns>固定した更新入力です。</returns>
    private static async Task<WebClientUpdateJob> PrepareAsync(SetupFixture fixture, bool service = false, CancellationToken cancellationToken = default)
    {
        await fixture.InitializeAsync(cancellationToken).ConfigureAwait(false);
        string entry = "Clutch-CS.ClientHost";
        if (OperatingSystem.IsWindows())
        {
            entry += ".exe";
        }
        string originalPayload = Path.Combine(fixture.Settings.PackageRoot, "client");
        ReleaseManifest original = await LocalUpdateExecutor.ReadManifestAsync(originalPayload, cancellationToken).ConfigureAwait(false);
        File.Move(Path.Combine(originalPayload, original.EntryPoint), Path.Combine(originalPayload, entry));
        await LocalUpdateExecutor.WriteManifestAsync(original with { EntryPoint = entry, Files = [original.Files.Single() with { Path = entry }] }, originalPayload, cancellationToken).ConfigureAwait(false);
        InstallationSettings settings = fixture.Settings with { WebClient = new(true, 55300, RunAsService: service) };
        InstallationRecord record = new(Guid.NewGuid(), settings, "fixture", "unused-core", "unused-updater", "unused-client", DateTimeOffset.UtcNow);
        WebClientInstallation client = (await new WebClientInstaller(fixture.Platform).InstallAsync(record, cancellationToken).ConfigureAwait(false))!;
        string payload = Path.Combine(fixture.Root, "next-client");
        Directory.CreateDirectory(payload);
        byte[] bytes = "new client release"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(payload, entry), bytes, cancellationToken).ConfigureAwait(false);
        ReleaseManifest manifest = new("fixture-v2", RuntimeInformation.RuntimeIdentifier, entry,
            [new(entry, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))], [], "fixture-v2");
        await LocalUpdateExecutor.WriteManifestAsync(manifest, payload, cancellationToken).ConfigureAwait(false);
        return new(Guid.NewGuid(), client, fixture.Platform.ClientProcess, manifest, payload);
    }
}
