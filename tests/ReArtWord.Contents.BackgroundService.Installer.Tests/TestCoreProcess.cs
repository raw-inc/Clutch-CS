using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>試験専用Coreを、SYSTEMまたは対話ユーザーの通常権限で起動します。</summary>
internal sealed class TestCoreProcess(string executable, string configuration, string serviceName) : IAsyncDisposable
{
    private readonly string taskName = "Clutch-CS-Test-Core-" + Guid.NewGuid().ToString("N");
    private Process? process;
    private bool service;

    /// <summary>試験で起動した実体が終了していないかです。</summary>
    internal bool Running => process is { HasExited: false };

    /// <summary>管理者環境ではサービスと同じSYSTEM、それ以外では通常ユーザーで開始します。</summary>
    /// <returns>起動したプロセスの所有確認です。</returns>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        service = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string arguments = "--config \"" + configuration + "\"";
        string launch = "Start-Process -FilePath " + Quote(executable) + " -ArgumentList " + Quote(arguments)
            + " -WorkingDirectory " + Quote(Path.GetDirectoryName(executable)!) + " -WindowStyle Hidden -Wait";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(launch));
        string powershellArguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded;
        string command = $"""
            $action = New-ScheduledTaskAction -Execute {Quote(PowerShell)} -Argument {Quote(powershellArguments)}
            $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
            Register-ScheduledTask -TaskName {Quote(taskName)} -Action $action -Principal $principal -Settings $settings -Description {Quote(taskName)} | Out-Null
            Start-ScheduledTask -TaskName {Quote(taskName)}
            """;
        if (service)
        {
            // SYSTEMタスクのJob Objectは別セッションへの子プロセス起動を制限するため、製品と同じWindowsサービスを使います。
            // https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject
            string binaryPath = "\"" + executable + "\" " + arguments;
            command = $"""
                New-Service -Name {Quote(serviceName)} -BinaryPathName {Quote(binaryPath)} -StartupType Manual -Description {Quote(taskName)} | Out-Null
                Start-Service -Name {Quote(serviceName)}
                """;
        }
        await RunAsync(command, cancellationToken).ConfigureAwait(false);
        while (process is null)
        {
            process = FindOwned();
            if (process is null)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>所有を確認したサービスまたはタスクと、専用配置のCoreだけを停止します。</summary>
    /// <returns>後続試験へ登録やプロセスを残さない後始末です。</returns>
    public async ValueTask DisposeAsync()
    {
        string command = $$"""
            $task = Get-ScheduledTask -TaskName {{Quote(taskName)}} -ErrorAction SilentlyContinue
            if ($task) {
                if ($task.Description -ne {{Quote(taskName)}}) { throw 'The task belongs to another test.' }
                Stop-ScheduledTask -TaskName {{Quote(taskName)}}
                Unregister-ScheduledTask -TaskName {{Quote(taskName)}} -Confirm:$false
            }
            """;
        if (service)
        {
            command = $$"""
                $service = Get-CimInstance Win32_Service -Filter {{Quote("Name='" + serviceName + "'")}}
                if ($service) {
                    if ($service.Description -ne {{Quote(taskName)}}) { throw 'The service belongs to another test.' }
                    Stop-Service -Name {{Quote(serviceName)}}
                    & "$env:SystemRoot/System32/sc.exe" delete {{Quote(serviceName)}} | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'The test service could not be removed.' }
                }
                """;
        }
        try
        {
            await RunAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            using Process? owned = process ?? FindOwned();
            if (owned is { HasExited: false })
            {
                owned.Kill(false);
                await owned.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>接続先や他の試験のCoreをPIDだけで取り違えません。</summary>
    /// <returns>この専用配置から起動した実体、または未起動のnullです。</returns>
    // 抑制理由: 一致したプロセスだけ所有フィールドへ渡し、それ以外はfinallyで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "返却したプロセスは試験のDisposeAsyncが所有し、不一致の候補はfinallyで解放します。")]
    private Process? FindOwned()
    {
        Process? owned = null;
        foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            bool matched = false;
            try
            {
                matched = owned is null && string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
                if (matched)
                {
                    owned = candidate;
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                // 他ユーザーのプロセスや、列挙中に終了したプロセスには触れません。
            }
            finally
            {
                using Process? unused = matched ? null
                                                : candidate;
            }
        }
        return owned;
    }

    /// <summary>PowerShellの単一引用符内へ固定した試験パスを渡します。</summary>
    /// <returns>コマンドとして評価されない文字列リテラルです。</returns>
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>登録と解除だけを期限付きの非表示プロセスで実行します。</summary>
    /// <returns>登録操作の完了です。</returns>
    private static async Task RunAsync(string command, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + command));
        _ = await BoundedProcessRunner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", encoded], timeout.Token).ConfigureAwait(false);
    }
}
