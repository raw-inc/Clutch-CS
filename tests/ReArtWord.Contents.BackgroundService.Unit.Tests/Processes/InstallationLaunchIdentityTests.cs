using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>OSの実体が同じ場合だけインストール成功の根拠にできることを確認します。</summary>
public sealed class InstallationLaunchIdentityTests
{
    /// <summary>時計が巻き戻っても正しい実体を受け付け、別PID、開始時刻の差、別パスを拒否します。</summary>
    [Fact]
    public void LaunchProofSurvivesClockRollbackWithoutAcceptingOtherProcesses()
    {
        using Process process = Process.GetCurrentProcess();
        ProcessIdentity actual = ProcessOs.Identity(process);
        string entry = process.MainModule!.FileName;
        DateTimeOffset beforeRollback = actual.StartedAt.AddHours(1);
        Assert.True(InstallationLaunchIdentity.Matches(actual, entry, actual, beforeRollback));
        Assert.False(InstallationLaunchIdentity.Matches(actual, entry, null, beforeRollback));
        Assert.True(InstallationLaunchIdentity.Matches(actual, entry, null, actual.StartedAt.AddSeconds(-1)));
        Assert.False(InstallationLaunchIdentity.Matches(actual, entry, actual with { ProcessId = actual.ProcessId + 1 }, beforeRollback));
        ProcessIdentity reused = actual with { StartedAt = actual.StartedAt.AddTicks(1) };
        Assert.False(InstallationLaunchIdentity.Matches(reused, entry, reused, beforeRollback));
        Assert.False(InstallationLaunchIdentity.Matches(actual, entry + ".other", actual, beforeRollback));
    }
}
