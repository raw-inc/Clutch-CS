using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>候補が存在するときだけOSから引数を取得し、コマンド本文へ利用者入力を展開しません。</summary>
internal static partial class ProcessArguments
{
    private static int MaximumCharacters => 131072;

    /// <summary>取得不能を空の引数とみなさず、対応するOS境界だけを使用します。</summary>
    /// <returns>実行ファイル名を除いた引数、取得不能時はnullです。</returns>
    internal static async Task<string[]?> ReadAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return await WindowsAsync(pid, cancellationToken).ConfigureAwait(false);
        }
        if (OperatingSystem.IsLinux())
        {
            string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline";
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            using StreamReader reader = new(stream, Encoding.UTF8);
            char[] buffer = new char[MaximumCharacters + 1];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0 || MaximumCharacters < read)
            {
                return null;
            }
            int length = buffer[read - 1] == '\0' ? read - 1
                                                  : read;
            return new string(buffer, 0, length).Split('\0').Skip(1).ToArray();
        }
        return null;
    }

    /// <summary>WMIの公開CommandLineを一PIDだけ照会し、Windowsの引用規則で分解します。</summary>
    /// <returns>正常に確認できた引数です。</returns>
    [SupportedOSPlatform("windows")]
    private static async Task<string[]?> WindowsAsync(int pid, CancellationToken cancellationToken = default)
    {
        string script = "$ErrorActionPreference='Stop'; [Console]::InputEncoding=New-Object System.Text.UTF8Encoding($false); [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false); $number=[int][Console]::In.ReadToEnd(); $item=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$number) -Property CommandLine; if($null -eq $item){exit 1}; [Console]::Out.Write([string]$item.CommandLine)";
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string commandLine = await BoundedProcessRunner.RunAsync(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            new ProcessRunLimits(MaximumCharacters, true, StandardInput: pid.ToString(CultureInfo.InvariantCulture), OutputEncoding: Encoding.UTF8), timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        nint values = CommandLineToArgv(commandLine, out int count);
        if (values == 0)
        {
            return null;
        }
        try
        {
            List<string> arguments = [];
            for (int index = 1; index < count; index++)
            {
                arguments.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(values, index * IntPtr.Size)) ?? string.Empty);
            }
            return arguments.ToArray();
        }
        finally
        {
            _ = LocalFree(values);
        }
    }

    /// <summary>OSと同じ引用・空白の規則で引数へ分解します。</summary>
    /// <returns>LocalFreeで一度だけ解放する配列です。</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int count);

    /// <summary>OSが確保した引数配列を解放します。</summary>
    /// <returns>解放成功時はnullです。</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
