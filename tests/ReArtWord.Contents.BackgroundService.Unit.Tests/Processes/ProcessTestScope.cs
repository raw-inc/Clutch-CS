using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>実プロセスとローカル保存を試験ごとに分離します。</summary>
internal sealed class ProcessTestScope : IDisposable
{
    private readonly List<ProcessIdentity> owned = [];

    /// <summary>試験用の構成の許可対象実行物と保存先を固定します。</summary>
    internal ProcessTestScope()
    {
        Root = Path.Combine(Path.GetTempPath(), "contents-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Repository = new SqliteRepository(Path.Combine(Root, "state.db"));
        Options = new ProcessOptions(1, 100, 1, 10);
        string executable = "/bin/sh";
        string[] arguments = ["-c", "exec sleep 60"];
        if (OperatingSystem.IsWindows())
        {
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
            arguments = ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"];
        }
        Definition = new ProcessDefinition("test-app", EProcessMode.Managed, executable, arguments, Path.GetTempPath());
    }

    /// <summary>この試験専用の保存先のルートです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>実際に再読取する永続リポジトリです。</summary>
    internal IDurableRepository Repository
    {
        get;
    }

    /// <summary>短い有限条件の共通設定です。</summary>
    internal ProcessOptions Options
    {
        get;
    }

    /// <summary>外部依存のない待機プロセス定義です。</summary>
    internal ProcessDefinition Definition
    {
        get;
    }

    /// <summary>同じ永続状態からサービスを作り直します。</summary>
    /// <returns>呼出側が破棄するサービスです。</returns>
    internal ProcessService CreateService()
    {
        return new ProcessService(Repository, "peer", Options);
    }

    /// <summary>試験で起動した実プロセスだけをcleanup対象へ登録します。</summary>
    internal void Track(ProcessOperationResult result)
    {
        if (result.Status?.Identity is not null)
        {
            owned.Add(result.Status.Identity);
        }
    }

    /// <summary>サービス外からの異常終了を実プロセスで起こします。</summary>
    /// <returns>実終了確認の完了です。</returns>
    internal static async Task CrashAsync(ProcessIdentity identity, CancellationToken cancellationToken = default)
    {
        using Process process = Process.GetProcessById(identity.ProcessId);
        process.Kill(true);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>試験が作成したプロセスと保存先のルートだけを取り除きます。</summary>
    // 抑制理由: PID再利用先を終了しないためBCLのStartTimeをUTCのDateTimeOffsetへ即時変換して比較します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験所有processだけをcleanupするOS識別子の照合に必要です。")]
    public void Dispose()
    {
        foreach (ProcessIdentity identity in owned)
        {
            try
            {
                using Process process = Process.GetProcessById(identity.ProcessId);
                if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == identity.StartedAt)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 試験途中で終了したプロセスは既にcleanup済みです。
            }
        }
        Directory.Delete(Root, true);
    }
}
