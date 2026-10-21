using System.Collections.Immutable;
using System.Runtime.InteropServices;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging.Signing;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>SDKやMSBuildの実行を必要とせず、現在の.NETとOS向けの依存関係を復元します。</summary>
internal static class NugetScriptPackages
{
    /// <summary>固定版の直接依存と推移依存を、一つの互換性検査済み環境にまとめます。</summary>
    /// <returns>コンパイル用参照と実行時資産を分離した構成です。</returns>
    internal static async Task<ScriptPackageEnvironment> InstallAsync(IReadOnlyList<ScriptPackageReference> packages, string directory,
        string cacheDirectory, string source, CancellationToken cancellationToken = default)
    {
        NuGetFramework framework = NuGetFramework.ParseFolder("net10.0");
        TargetFrameworkInformation information = new()
        {
            FrameworkName = framework,
            RuntimeIdentifierGraphPath = Path.Combine(directory, "runtime-identifiers.json"),
            Dependencies = packages.Select(package => new LibraryDependency
            {
                LibraryRange = new(package.Id, VersionRange.Parse("[" + package.Version + "]"), LibraryDependencyTarget.Package),
            }).ToImmutableArray(),
        };
        string project = Path.Combine(directory, "project.json");

        // RIDの継承表は資産の選択に使い、全OSを復元対象として列挙しません。
        using (Stream runtimeIdentifiers = typeof(NugetScriptPackages).Assembly.GetManifestResourceStream("Contents.ScriptRuntimeIdentifiers.json")!)
        using (FileStream output = new(information.RuntimeIdentifierGraphPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await runtimeIdentifiers.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        PackageSpec specification = new([information])
        {
            Name = "ContentsScripts",
            Version = NuGetVersion.Parse("1.0.0"),
            FilePath = project,
            RestoreMetadata = new()
            {
                ProjectStyle = ProjectStyle.PackageReference,
                ProjectUniqueName = project,
                ProjectName = "ContentsScripts",
                ProjectPath = project,
                OutputPath = directory,
                OriginalTargetFrameworks = ["net10.0"],
                TargetFrameworks = [new ProjectRestoreMetadataFrameworkInfo(framework)],
            },
        };
        using SourceCacheContext cache = new();
        ILogger logger = NullLogger.Instance;
        RestoreCommandProviders providers = new RestoreCommandProvidersCache().GetOrCreate(cacheDirectory, [], [Repository.Factory.GetCoreV3(source)], cache, logger);
        RestoreRequest request = new(specification, providers, cache, ClientPolicyContext.GetClientPolicy(NullSettings.Instance, logger),
            PackageSourceMapping.GetPackageSourceMapping(NullSettings.Instance), logger, new LockFileBuilderCache())
        {
            ProjectStyle = ProjectStyle.PackageReference,
            LockFilePath = Path.Combine(directory, "project.assets.json"),

            // 直接のNuGet API呼出しでも通常のPackageReferenceの方針を引き継ぎます。
            ValidateRuntimeAssets = specification.RestoreMetadata.ValidateRuntimeAssets,
        };
        request.RequestedRuntimes.Add(RuntimeInformation.RuntimeIdentifier);
        RestoreResult result = await new RestoreCommand(request).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        await result.CommitAsync(logger, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.LockFile.LogMessages.Any(message => message.Code is NuGetLogCode.NU1605 or NuGetLogCode.NU1608))
        {
            throw new InvalidDataException("script.packages.nugetRestoreFailed: " + string.Join("; ", result.LockFile.LogMessages.Take(8).Select(message => message.Code.ToString())));
        }
        LockFileTarget target = result.LockFile.Targets.Single(item => string.Equals(item.RuntimeIdentifier, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal));
        if (512 < target.Libraries.Count)
        {
            throw new InvalidDataException("script.packages.dependencyLimit");
        }
        List<string> references = [];
        List<string> assemblies = [];
        List<string> native = [];
        foreach (LockFileTargetLibrary library in target.Libraries)
        {
            string path = result.LockFile.Libraries.Single(item => string.Equals(item.Name, library.Name, StringComparison.OrdinalIgnoreCase) && item.Version == library.Version).Path;
            string packageDirectory = Path.GetFullPath(Path.Combine(cacheDirectory, path));
            references.AddRange(Assets(packageDirectory, library.CompileTimeAssemblies));
            assemblies.AddRange(Assets(packageDirectory, library.RuntimeAssemblies.Concat(library.ResourceAssemblies)));
            native.AddRange(Assets(packageDirectory, library.NativeLibraries));
        }
        ScriptPackageReference[] installed = target.Libraries.Select(library => new ScriptPackageReference(ScriptPackageNames.Normalize(library.Name!, EScriptPackageKind.Nuget), library.Version!.ToNormalizedString()))
            .OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
        ScriptPackageEnvironmentInfo info = new(EScriptPackageKind.Nuget, ScriptPackageRules.Identity(EScriptPackageKind.Nuget, packages, installed), packages, installed, DateTimeOffset.UtcNow);
        return new(info, references, assemblies, native);
    }

    /// <summary>パッケージ内の空資産印を除き、実在する資産だけを公開します。</summary>
    /// <returns>復元ディレクトリー内の絶対パスです。</returns>
    private static IEnumerable<string> Assets(string directory, IEnumerable<LockFileItem> items)
    {
        string empty = "_._";
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        foreach (LockFileItem item in items)
        {
            if (string.Equals(Path.GetFileName(item.Path), empty, StringComparison.Ordinal))
            {
                continue;
            }
            string path = Path.GetFullPath(Path.Combine(directory, item.Path));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, comparison) || !File.Exists(path))
            {
                throw new InvalidDataException("script.packages.assetUnavailable");
            }
            yield return path;
        }
    }
}
