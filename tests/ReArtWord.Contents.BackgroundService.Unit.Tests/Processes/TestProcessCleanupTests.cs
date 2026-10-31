using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>一件の回収失敗で、別の試験プロセスを残さないことを確認します。</summary>
public sealed class TestProcessCleanupTests
{
    /// <summary>状態を照会できないプロセスが先頭でも、起動済みの二つは終了します。</summary>
    /// <returns>失敗の通知と、実プロセスの終了確認です。</returns>
    [Fact]
    public async Task StopsOtherProcessesBeforeReportingACleanupFailureAsync()
    {
        using ApplicationProcessFixture fixture = new(delayMilliseconds: -1);
        using Process unavailable = new();
        List<Process?> owned = [unavailable, null];
        List<Process> observers = [];
        try
        {
            for (int index = 0; index < 2; index++)
            {
                ProcessStartInfo start = new(fixture.Definition.ExecutablePath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root,
                };
                Process process = Process.Start(start) ?? throw new IOException("The cleanup fixture did not start.");
                owned.Add(process);
                Process observer = Process.GetProcessById(process.Id);
                observers.Add(observer);
                Assert.False(observer.HasExited);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestProcessCleanup.StopAsync(owned.ToArray())).ConfigureAwait(true);
            Assert.All(observers, observer => Assert.True(observer.HasExited));
        }
        finally
        {
            try
            {
                await TestProcessCleanup.StopAsync(observers.ToArray()).ConfigureAwait(true);
            }
            finally
            {
                foreach (Process? process in owned)
                {
                    process?.Dispose();
                }
            }
        }
    }
}
