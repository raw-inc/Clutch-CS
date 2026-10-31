using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>並列試験で起動した待機プロセスを、別試験の後継として引き継がないことを確認します。</summary>
public sealed class ProcessFixtureIsolationTests
{
    /// <summary>登録の照合条件を試験ごとに分け、他の試験を後継候補へ含めません。</summary>
    /// <returns>別の保存先に属する実プロセスを採用しない照合結果です。</returns>
    [Fact]
    public async Task AnotherScopeDoesNotSupplyAReplacementAsync()
    {
        using ProcessTestScope first = new();
        using ProcessTestScope second = new();
        using ProcessService service = second.CreateService();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(second.Definition, cancellationToken: timeout.Token).ConfigureAwait(true));
        ProcessOperationResult started = await service.StartAsync(second.Definition.Id, timeout.Token).ConfigureAwait(true);
        second.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        ProcessReplacementWindow window = new(DateTimeOffset.UtcNow.AddMinutes(1), notBefore, first.Definition.ExecutablePath,
            ContractJson.Hash(first.Definition.Arguments), EProcessRestartMatch.ExecutableAndArguments);
        (ProcessIdentity? identity, string? problem) = await new ProcessReplacementProbe().FindAsync(first.Definition.Id, window, timeout.Token).ConfigureAwait(true);
        Assert.Null(identity);
        Assert.Null(problem);
        Assert.True((await service.ObserveAsync(second.Definition.Id, timeout.Token).ConfigureAwait(true)).Status!.Running);
    }
}
