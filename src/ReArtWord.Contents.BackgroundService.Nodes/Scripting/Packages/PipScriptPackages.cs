using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>システムPythonを変更せず、移動しない専用仮想環境へ固定版を導入します。</summary>
internal static class PipScriptPackages
{
    /// <summary>対応するPythonとその世代を実行先で確認します。</summary>
    /// <returns>構成の固定に含めるPython版です。</returns>
    internal static async Task<string> VersionAsync(string pythonPath, CancellationToken cancellationToken = default)
    {
        string version = await BoundedProcessRunner.RunAsync(pythonPath, ["-I", "-S", "-c", "import sys; assert sys.version_info >= (3, 10); print(sys.version.split()[0])"],
            PythonRuntime.Limits(1024, 268435456), cancellationToken).ConfigureAwait(false);
        return version.Trim();
    }

    /// <summary>導入後の依存関係検査まで成功した環境だけを返します。</summary>
    /// <returns>実際に解決した版と、この構成専用のPythonです。</returns>
    internal static async Task<ScriptPackageEnvironment> InstallAsync(IReadOnlyList<ScriptPackageReference> packages, string pythonPath, string pythonVersion,
        string directory, string? wheelDirectory, CancellationToken cancellationToken = default)
    {
        string environment = Path.Combine(directory, "venv");
        await RunStageAsync(pythonPath, ["-I", "-m", "venv", environment], "script.packages.venvFailed", cancellationToken).ConfigureAwait(false);
        string executable = Path.Combine(environment, "bin/python");
        if (OperatingSystem.IsWindows())
        {
            executable = Path.Combine(environment, "Scripts/python.exe");
        }
        if (packages.Count != 0)
        {
            List<string> arguments = ["-I", "-m", "pip", "--isolated", "--require-virtualenv", "--disable-pip-version-check", "install", "--no-input", "--progress-bar", "off", "--quiet"];
            if (wheelDirectory is not null)
            {
                arguments.AddRange(["--no-index", "--find-links", wheelDirectory]);
            }
            else
            {
                arguments.AddRange(["--index-url", "https://pypi.org/simple"]);
            }
            arguments.AddRange(packages.Select(package => package.Id + "==" + package.Version));
            await RunStageAsync(executable, arguments, "script.packages.pipInstallFailed", cancellationToken).ConfigureAwait(false);
        }
        await RunStageAsync(executable, ["-I", "-m", "pip", "--isolated", "--require-virtualenv", "--disable-pip-version-check", "check"], "script.packages.pipCheckFailed", cancellationToken).ConfigureAwait(false);
        string listing = await BoundedProcessRunner.RunAsync(executable, ["-I", "-m", "pip", "--isolated", "--require-virtualenv", "--disable-pip-version-check", "list", "--format=json"],
            PythonRuntime.Limits(1048576, 1073741824), cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(listing);
        ScriptPackageReference[] installed = document.RootElement.EnumerateArray().Select(item => new ScriptPackageReference(
            ScriptPackageNames.Normalize(item.GetProperty("name").GetString()!, EScriptPackageKind.Pip),
            item.GetProperty("version").GetString()!)).OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
        if (512 < installed.Length || packages.Any(package => !installed.Any(item => string.Equals(item.Id, package.Id, StringComparison.Ordinal))))
        {
            throw new InvalidDataException("script.packages.dependencyLimitOrMissing");
        }
        ScriptPackageEnvironmentInfo info = new(EScriptPackageKind.Pip, ScriptPackageRules.Identity(EScriptPackageKind.Pip, packages, installed, pythonVersion),
            packages, installed, DateTimeOffset.UtcNow, pythonPath, pythonVersion);
        return new(info, [], [], [], executable);
    }

    /// <summary>処理段階を返し、インストーラーの任意の出力を管理応答へ混入させません。</summary>
    /// <returns>有限プロセスの正常終了です。</returns>
    private static async Task RunStageAsync(string executable, IReadOnlyList<string> arguments, string failure, CancellationToken cancellationToken = default)
    {
        InvalidDataException? failed = null;
        try
        {
            await BoundedProcessRunner.RunAsync(executable, arguments, PythonRuntime.Limits(1048576, 1073741824, requireOutput: false), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            failed = exception;
        }
        if (failed is not null)
        {
            throw new InvalidDataException(failure, failed);
        }
    }
}
