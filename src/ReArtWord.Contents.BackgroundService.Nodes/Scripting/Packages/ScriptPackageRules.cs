using System.Text.RegularExpressions;
using NuGet.Versioning;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>配布名・版・実行先をCLIのオプションや別の保存パスとして解釈させません。</summary>
internal static class ScriptPackageRules
{
    /// <summary>固定した入力を正規化し、不明な管理方式を拒否します。</summary>
    /// <returns>同じ要求を識別できる正規化済みの入力です。</returns>
    internal static ScriptPackageChangeRequest Validate(ScriptPackageChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string versionPattern = "^[0-9][A-Za-z0-9.!+_-]{0,127}$";
        if (request.Kind == EScriptPackageKind.Npm)
        {
            versionPattern = "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$";
        }
        if (request.OperationId == Guid.Empty || !Enum.IsDefined(request.Kind) || request.Package is null
            || !ScriptPackageNames.Valid(request.Package.Id, request.Kind)
            || request.ExpectedRevision is not null && !Revision(request.ExpectedRevision))
        {
            throw new ArgumentException("script.packages.invalidRequest", nameof(request));
        }
        string id = ScriptPackageNames.Normalize(request.Package.Id!, request.Kind);
        string version = request.Package.Version?.Trim() ?? string.Empty;
        if (128 < version.Length)
        {
            throw new ArgumentException("script.packages.versionRequired", nameof(request));
        }
        if (!request.Remove)
        {
            if (request.Kind == EScriptPackageKind.Nuget)
            {
                if (!NuGetVersion.TryParse(version, out NuGetVersion? parsed))
                {
                    throw new ArgumentException("script.packages.versionRequired", nameof(request));
                }
                version = parsed.ToNormalizedString();
            }
            else
            {
                if (!Regex.IsMatch(version, versionPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    throw new ArgumentException("script.packages.versionRequired", nameof(request));
                }
            }
        }
        return request with { Package = new(id, version), PythonPath = RuntimePath(request.PythonPath), NodePath = RuntimePath(request.NodePath), NpmPath = RuntimePath(request.NpmPath) };
    }

    /// <summary>ランタイムやCLIをPATH検索へ委ねず、明示した絶対パスに固定します。</summary>
    /// <returns>正規化したパス、または管理中の値を再使用するnullです。</returns>
    private static string? RuntimePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (4096 < path.Length || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("script.packages.runtimePathRequired", nameof(path));
        }
        return Path.GetFullPath(path);
    }

    /// <summary>保存先名として使用できる完全な構成指紋かを確認します。</summary>
    /// <returns>SHA-256の16進表記かです。</returns>
    internal static bool Revision(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    /// <summary>PC固有の配置先を含めず、直接・推移依存の構成とPython版を識別します。</summary>
    /// <returns>同じ構成を実行先で照会する識別子です。</returns>
    internal static string Identity(EScriptPackageKind kind, IReadOnlyList<ScriptPackageReference> packages, IReadOnlyList<ScriptPackageReference> installed, string? pythonVersion = null)
        => ContractJson.Hash(new { Kind = kind, Packages = packages.OrderBy(package => package.Id, StringComparer.Ordinal).ToArray(),
            Installed = installed.OrderBy(package => package.Id, StringComparer.Ordinal).ToArray(), PythonVersion = pythonVersion });
}
