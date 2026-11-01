using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>引数照会中の終了と、稼働中の候補を確認できない状態を区別します。</summary>
public sealed class ProcessReplacementProbeTests
{
    /// <summary>照会失敗で監視を中断せず、生存が不明な候補を候補なしとして扱いません。</summary>
    /// <returns>実プロセスの終了状態に対応する候補照合結果です。</returns>
    [Theory]
    [InlineData(false, "command")]
    [InlineData(true, "command")]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "io")]
    [InlineData(true, "io")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    public async Task ArgumentReadFailurePreservesCandidateUncertaintyAsync(bool exits, string failure)
    {
        using ApplicationProcessFixture application = new(delayMilliseconds: -1);
        using Process candidate = ApplicationReplacementTests.StartSuccessor(application, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await ApplicationRecoveryTests.WaitForStartAsync(application, timeout.Token).ConfigureAwait(true);
        int calls = 0;
        ProcessReplacementProbe probe = new(async (pid, token) =>
        {
            Assert.Equal(candidate.Id, pid);
            calls++;
            if (exits)
            {
                candidate.Kill(true);
                await ProcessCompletion.WaitAsync(candidate, token).ConfigureAwait(false);
            }
            throw failure switch
            {
                "command" => new InvalidDataException("fixture argument command failed"),
                "stop" => new ProcessStopUnconfirmedException(),
                "io" => new IOException("fixture argument read failed"),
                _ => new OperationCanceledException("fixture query timed out"),
            };
        });
        (ProcessIdentity? identity, string? problem) = await probe.FindAsync("application", Window(application, candidate), timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, calls);
        Assert.Null(identity);
        string? expected = "process.replacementUnconfirmed";
        if (exits)
        {
            expected = null;
        }
        Assert.Equal(expected, problem);
        Assert.Equal(exits, candidate.HasExited);
    }

    /// <summary>確認できた候補が一つあっても、別の稼働候補の照会失敗を無視しません。</summary>
    /// <returns>誤った一意判定を防ぐ結果不明です。</returns>
    [Fact]
    public async Task AnUnreadableCandidatePreventsAdoptingAnotherAsync()
    {
        using ApplicationProcessFixture application = new(delayMilliseconds: -1);
        using Process first = ApplicationReplacementTests.StartSuccessor(application, false);
        using Process second = ApplicationReplacementTests.StartSuccessor(application, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (application.Starts().Length < 2)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        ProcessReplacementProbe probe = new((pid, _) =>
        {
            if (pid == first.Id)
            {
                return Task.FromResult<string[]?>(application.Definition.Arguments.ToArray());
            }
            return Task.FromException<string[]?>(new InvalidDataException("fixture unreadable candidate"));
        });
        (ProcessIdentity? identity, string? problem) = await probe.FindAsync("application", Window(application, first), timeout.Token).ConfigureAwait(true);
        Assert.Null(identity);
        Assert.Equal("process.replacementUnconfirmed", problem);
        Assert.False(first.HasExited);
        Assert.False(second.HasExited);
    }

    /// <summary>呼出元の取消は照合不能へ変換せず、そのまま伝えます。</summary>
    /// <returns>進行中の引数照会を取り消した結果です。</returns>
    [Fact]
    public async Task CallerCancellationIsNotAProbeFailureAsync()
    {
        using ApplicationProcessFixture application = new(delayMilliseconds: -1);
        using Process candidate = ApplicationReplacementTests.StartSuccessor(application, false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await ApplicationRecoveryTests.WaitForStartAsync(application, timeout.Token).ConfigureAwait(true);
        ProcessReplacementProbe probe = new(async (_, token) =>
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return [];
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.FindAsync("application", Window(application, candidate), timeout.Token).AsTask()).ConfigureAwait(true);
        Assert.False(candidate.HasExited);
    }

    /// <summary>この試験が起動した実行ファイルと、その開始時刻以後だけを照合します。</summary>
    /// <returns>他の試験や本番アプリケーションを候補に含めない条件です。</returns>
    private static ProcessReplacementWindow Window(ApplicationProcessFixture application, Process candidate)
        => new(DateTimeOffset.UtcNow.AddMinutes(1), ProcessOs.Identity(candidate).StartedAt, application.Definition.ExecutablePath,
            ContractJson.Hash(application.Definition.Arguments), EProcessRestartMatch.ExecutableAndArguments);
}
