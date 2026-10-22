using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>保守ファイルの障害をCore停止へ広げず、元操作の確認後だけ保留を解放します。</summary>
public sealed class SetupManagementTests
{
    /// <summary>helperがまだ公開していない状態を障害扱いせず、停止要求を待ちます。</summary>
    /// <returns>保留を維持した通常待機の確認です。</returns>
    [Fact]
    public async Task UnpublishedFilesKeepWaitingWithoutDiagnosticsAsync()
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(0, fixture.Lifetime.Stops);
        Assert.True(fixture.Maintenance.IsClosed);
        Assert.Empty(fixture.Log.Entries);
    }

    /// <summary>読取りロックや診断の失敗中も監視を継続し、解放後にだけ停止します。</summary>
    /// <returns>実ファイル競合と保守保留の復旧確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedStopRequestIsRetriedWithoutStoppingCoreAsync(bool failLog)
    {
        SetupMonitorFixture fixture = new(failLog);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        string signal = Path.Combine(fixture.DirectoryPath, "stop.request");
        await InstallationFiles.WriteTextAsync(signal, ContractJson.Hash(fixture.Job));
        using (FileStream locked = new(signal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
            Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
            Assert.Equal(0, fixture.Lifetime.Stops);
            Assert.Equal(fixture.Job.OperationId, fixture.Maintenance.State!.OperationId);
            Assert.Single(fixture.Log.Entries);
        }
        Assert.True(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(1, fixture.Lifetime.Stops);
        Assert.False(fixture.Maintenance.IsClosed);
    }

    /// <summary>読めない・不一致・結果不明の記録では、残存する停止要求を実行しません。</summary>
    /// <returns>結果の再確認後に、Coreを止めず元の保留だけを解除する確認です。</returns>
    [Theory]
    [InlineData("partial")]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("other-root")]
    [InlineData("invalid-outcome")]
    public async Task UnconfirmedResultPreservesMaintenanceUntilConfirmedAsync(string problem)
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        string resultPath = Path.Combine(fixture.DirectoryPath, "result.json");
        SetupResult result = new(fixture.Job.OperationId, EOutcome.Failed, "validate", "Rejected before replacement.", fixture.Job.Settings.Root);
        await InstallationFiles.WriteTextAsync(Path.Combine(fixture.DirectoryPath, "stop.request"), ContractJson.Hash(fixture.Job));
        switch (problem)
        {
            case "partial":
                await File.WriteAllTextAsync(resultPath, "{");
                break;
            case "foreign":
                await InstallationFiles.WriteAsync(resultPath, result with { OperationId = Guid.NewGuid() });
                break;
            case "unknown":
                await InstallationFiles.WriteAsync(resultPath, result with { Outcome = EOutcome.Unknown });
                break;
            case "other-root":
                await InstallationFiles.WriteAsync(resultPath, result with { Root = Path.Combine(fixture.Job.Settings.Root, "other") });
                break;
            default:
                await InstallationFiles.WriteAsync(resultPath, result with { Outcome = (EOutcome)999 });
                break;
        }
        Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(0, fixture.Lifetime.Stops);
        Assert.True(fixture.Maintenance.IsClosed);
        Assert.Single(fixture.Log.Entries);
        await InstallationFiles.WriteAsync(resultPath, result);
        Assert.True(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(0, fixture.Lifetime.Stops);
        Assert.False(fixture.Maintenance.IsClosed);
    }

    /// <summary>同じIDでも異なる入力の停止要求を採用しません。</summary>
    /// <returns>正しい要求へ置換された後だけ停止する確認です。</returns>
    [Fact]
    public async Task MismatchedStopRequestDoesNotReleaseMaintenanceAsync()
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        string signal = Path.Combine(fixture.DirectoryPath, "stop.request");
        await InstallationFiles.WriteTextAsync(signal, ContractJson.Hash(fixture.Job with { KeepDatabase = false }));
        Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.True(fixture.Maintenance.IsClosed);
        Assert.Equal(0, fixture.Lifetime.Stops);
        await InstallationFiles.WriteTextAsync(signal, ContractJson.Hash(fixture.Job));
        Assert.True(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(1, fixture.Lifetime.Stops);
    }

    /// <summary>別操作の保留を解除せず、Coreの停止にも進みません。</summary>
    /// <returns>所有者変更後の保留保持です。</returns>
    [Fact]
    public async Task ChangedMaintenanceOwnerPreventsStoppingAsync()
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        Assert.True(await fixture.Maintenance.ExitAsync(fixture.Job.OperationId));
        Guid other = Guid.NewGuid();
        Assert.True(await fixture.Maintenance.EnterAsync(other, "other"));
        await InstallationFiles.WriteTextAsync(Path.Combine(fixture.DirectoryPath, "stop.request"), ContractJson.Hash(fixture.Job));
        Assert.False(await fixture.Monitor.RefreshAsync(fixture.Job));
        Assert.Equal(other, fixture.Maintenance.State!.OperationId);
        Assert.Equal(0, fixture.Lifetime.Stops);
    }

    /// <summary>Host終了の取消を監視障害として記録せず、停止要求も追加しません。</summary>
    /// <returns>呼出元の取消を維持する確認です。</returns>
    [Fact]
    public async Task ShutdownCancellationDoesNotConsumeTheStopRequestAsync()
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync();
        using CancellationTokenSource stopped = new();
        await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor.RefreshAsync(fixture.Job, stopped.Token));
        Assert.Equal(0, fixture.Lifetime.Stops);
        Assert.Empty(fixture.Log.Entries);
        Assert.True(fixture.Maintenance.IsClosed);
    }

    /// <summary>停止要求の公開は読取り中の旧本文を壊さず、次の読取りへ完成した本文を渡します。</summary>
    /// <returns>原子的置換と文字列形式の互換性です。</returns>
    [Fact]
    public async Task StopRequestPublicationPreservesAnOpenReaderAsync()
    {
        SetupMonitorFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string signal = Path.Combine(fixture.DirectoryPath, "stop.request");
        await InstallationFiles.WriteTextAsync(signal, "previous");
        using FileStream stream = new(signal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        string current = ContractJson.Hash(fixture.Job);
        await InstallationFiles.WriteTextAsync(signal, current);
        Assert.Equal("previous", await reader.ReadToEndAsync());
        Assert.Equal(current, await File.ReadAllTextAsync(signal));
    }
}
