using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>一件の回収失敗で、別の試験プロセスを残さないことを確認します。</summary>
public sealed class TestProcessCleanupTests
{
    /// <summary>応答でPIDを取得できなくても、専用配置だけを停止し、似たパスの別配置は維持します。</summary>
    /// <returns>所有する実行ファイルの判定と終了待ちの確認です。</returns>
    [Fact]
    public async Task StopsUnobservedProcessWithoutStoppingAnotherInstallationAsync()
    {
        using ApplicationProcessFixture fixture = new(delayMilliseconds: -1);
        string otherRoot = Directory.CreateDirectory(fixture.Root + "-other").FullName;
        Process? owned = null;
        Process? other = null;
        try
        {
            foreach (string file in Directory.GetFiles(fixture.Root))
            {
                File.Copy(file, Path.Combine(otherRoot, Path.GetFileName(file)));
            }
            owned = Process.Start(new ProcessStartInfo(fixture.Definition.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new IOException("The owned cleanup fixture did not start.");
            other = Process.Start(new ProcessStartInfo(Path.Combine(otherRoot, Path.GetFileName(fixture.Definition.ExecutablePath))) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new IOException("The other cleanup fixture did not start.");
            Assert.False(owned.HasExited);
            Assert.False(other.HasExited);
            await TestProcessCleanup.StopDirectoryAsync(fixture.Root + Path.DirectorySeparatorChar).ConfigureAwait(true);
            Assert.True(owned.HasExited);
            Assert.False(other.HasExited);
        }
        finally
        {
            await TestProcessCleanup.StopAsync(owned, other).ConfigureAwait(true);
            Directory.Delete(otherRoot, true);
        }
    }

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
