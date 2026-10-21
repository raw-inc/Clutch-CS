using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.JavaScript;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>固定したlockfileを実際に導入し、npmの環境を既存世代から分けます。</summary>
internal static class NpmScriptPackages
{
    /// <summary>shellやnpm.cmdを介さず、指定Node.jsでnpm CLIを実行します。</summary>
    /// <returns>固定した依存グラフと実行時の探索先です。</returns>
    internal static async Task<ScriptPackageEnvironment> InstallAsync(IReadOnlyList<ScriptPackageReference> packages, string nodePath, string nodeVersion,
        string? npmPath, string directory, string cache, string registry, CancellationToken cancellationToken = default)
    {
        string cli = FindCli(nodePath, npmPath);
        string project = Path.Combine(directory, "npm");
        Directory.CreateDirectory(project);
        string userConfig = Path.Combine(project, "user.npmrc"), globalConfig = Path.Combine(project, "global.npmrc");
        await File.WriteAllTextAsync(userConfig, string.Empty, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(globalConfig, string.Empty, cancellationToken).ConfigureAwait(false);
        string manifest = JsonSerializer.Serialize(new { name = "contents-script-packages", version = "1.0.0", @private = true, type = "module",
            dependencies = packages.ToDictionary(package => package.Id, package => package.Version, StringComparer.Ordinal) }, ContractJson.Options);
        await File.WriteAllTextAsync(Path.Combine(project, "package.json"), manifest, cancellationToken).ConfigureAwait(false);
        string[] options = ["--prefix", project, "--global=false", "--userconfig", userConfig, "--globalconfig", globalConfig, "--cache", cache,
            "--registry", registry, "--audit=false", "--fund=false", "--strict-peer-deps", "--engine-strict", "--omit=dev", "--loglevel=error"];
        await RunAsync(nodePath, [cli, "install", "--package-lock-only", "--ignore-scripts", .. options], project, "script.packages.npmResolveFailed", cancellationToken).ConfigureAwait(false);
        string lockText = await File.ReadAllTextAsync(Path.Combine(project, "package-lock.json"), cancellationToken).ConfigureAwait(false);
        using JsonDocument graph = JsonDocument.Parse(lockText);
        JsonProperty[] entries = graph.RootElement.GetProperty("packages").EnumerateObject().Where(entry => entry.Name.Length != 0).ToArray();
        if (512 < entries.Length)
        {
            throw new InvalidDataException("script.packages.dependencyLimit");
        }
        await RunAsync(nodePath, [cli, "ci", "--ignore-scripts=false", .. options], project, "script.packages.npmInstallFailed", cancellationToken).ConfigureAwait(false);
        await RunAsync(nodePath, [cli, "ls", "--all", "--json", .. options], project, "script.packages.npmCheckFailed", cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(project, "node_modules"));
        List<ScriptPackageReference> installed = [];
        foreach (JsonProperty entry in entries)
        {
            string relative = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            string path = Path.GetFullPath(Path.Combine(project, relative));
            string prefix = project + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("script.packages.assetUnavailable");
            }
            if (Directory.Exists(path))
            {
                using JsonDocument package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "package.json"), cancellationToken).ConfigureAwait(false));
                installed.Add(new(package.RootElement.GetProperty("name").GetString()!, package.RootElement.GetProperty("version").GetString()!));
            }
        }
        string revision = ContractJson.Hash(new { Kind = EScriptPackageKind.Npm, Requested = packages, NodeVersion = nodeVersion, Graph = graph.RootElement });
        ScriptPackageEnvironmentInfo info = new(EScriptPackageKind.Npm, revision, packages,
            installed.Distinct().OrderBy(package => package.Id, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow, NodePath: nodePath, NodeVersion: nodeVersion, NpmPath: cli);
        return new(info, [], [], [], NpmDirectory: project);
    }

    /// <summary>Node.js配布とOS標準のnpm配置だけを確認し、見つからなければ明示指定を要求します。</summary>
    /// <returns>Node.jsで直接起動できるnpm-cli.jsの絶対パスです。</returns>
    private static string FindCli(string nodePath, string? npmPath)
    {
        if (npmPath is not null)
        {
            if (!File.Exists(npmPath))
            {
                throw new InvalidDataException("script.packages.npmPathRequired");
            }
            return npmPath;
        }
        string directory = Path.GetDirectoryName(nodePath)!;
        string[] candidates = [Path.Combine(directory, "node_modules/npm/bin/npm-cli.js"), Path.Combine(directory, "../lib/node_modules/npm/bin/npm-cli.js"),
            Path.Combine(directory, "../share/nodejs/npm/bin/npm-cli.js")];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? throw new InvalidDataException("script.packages.npmPathRequired");
    }

    /// <summary>導入段階を維持し、外部パッケージの任意の出力は応答へ混ぜません。</summary>
    /// <returns>期限付きのnpm処理完了です。</returns>
    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string directory, string reason, CancellationToken cancellationToken = default)
    {
        InvalidDataException? failed = null;
        try
        {
            await BoundedProcessRunner.RunAsync(executable, arguments, JavaScriptRuntime.Limits(2097152, 1073741824, directory, executable), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            failed = exception;
        }
        if (failed is not null)
        {
            throw new InvalidDataException(reason, failed);
        }
    }
}
