using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

/// <summary>実行中・保存済みの構成を保持し、完成した環境だけを次の解析へ公開します。</summary>
public sealed class ScriptPackageStore
{
    private readonly Lock gate = new();
    private readonly string root;

    /// <summary>サービス更新で置換されないPeerのデータ領域へ保存します。</summary>
    public ScriptPackageStore(string dataDirectory)
    {
        root = Path.Combine(Path.GetFullPath(dataDirectory), "script-packages");
        Directory.CreateDirectory(Path.Combine(root, "environments"));
        Directory.CreateDirectory(Path.Combine(root, "operations"));
    }

    /// <summary>NuGetの版別キャッシュを複数の構成で共有します。</summary>
    internal string NugetCache => Path.Combine(root, "nuget-cache");

    /// <summary>npmの取得キャッシュもサービスのデータ領域に限定します。</summary>
    internal string NpmCache => Path.Combine(root, "npm-cache");

    /// <summary>失敗・中断した導入先と新しい導入先を共有しません。</summary>
    /// <returns>移動せず使用する一回専用の作業先です。</returns>
    internal string Workspace(Guid operationId)
    {
        string path = Path.Combine(root, "builds", operationId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>新しい解析が使用する構成の説明を取得します。</summary>
    /// <returns>導入済みの構成、または標準環境です。</returns>
    public ScriptPackageEnvironmentInfo? Current(EScriptPackageKind kind)
    {
        using (gate.EnterScope())
        {
            return ScriptRecordFile.Read<ScriptPackageEnvironmentInfo>(CurrentPath(kind));
        }
    }

    /// <summary>明示Python実行先を尊重し、それ以外は現在のパッケージ構成で解析します。</summary>
    /// <returns>一回の解析で固定する環境です。</returns>
    public ScriptPackageEnvironment ForAnalysis(ScriptAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EScriptPackageKind kind = request.Language switch { "python" => EScriptPackageKind.Pip, "javascript" => EScriptPackageKind.Npm, _ => EScriptPackageKind.Nuget };
        string? runtimePath = request.Language switch { "python" => request.PythonPath, "javascript" => request.NodePath, _ => null };
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        ScriptPackageEnvironmentInfo? current = Current(kind);
        string? currentPath = current?.Kind switch { EScriptPackageKind.Pip => current.PythonPath, EScriptPackageKind.Npm => current.NodePath, _ => null };
        if (current is null || !string.IsNullOrWhiteSpace(runtimePath) && !string.Equals(runtimePath, currentPath, comparison))
        {
            return ScriptPackageEnvironment.Empty;
        }
        return Resolve(current.Kind, current.Revision);
    }

    /// <summary>保存時に固定された環境を照会し、現在構成への暗黙の差し替えを拒否します。</summary>
    /// <returns>依存関係を変更しない実行環境です。</returns>
    public ScriptPackageEnvironment Resolve(EScriptPackageKind kind, string? revision)
    {
        if (revision is null)
        {
            return ScriptPackageEnvironment.Empty;
        }
        using (gate.EnterScope())
        {
            ScriptPackageEnvironment? environment = Find(revision);
            string lockFile = "package-lock.json", modules = "node_modules";
            if (environment?.Info?.Kind != kind || environment.References.Concat(environment.Assemblies).Concat(environment.NativeLibraries).Any(path => !File.Exists(path))
                || kind == EScriptPackageKind.Pip && !File.Exists(environment.PythonExecutable)
                || kind == EScriptPackageKind.Npm && (!File.Exists(environment.Info.NodePath) || environment.NpmDirectory is null
                    || !File.Exists(Path.Combine(environment.NpmDirectory, lockFile)) || !Directory.Exists(Path.Combine(environment.NpmDirectory, modules))))
            {
                throw new InvalidOperationException("script.packages.environmentUnavailable");
            }
            return environment;
        }
    }

    /// <summary>同じ明示構成を再度選んだときは、以前に復元した依存関係を再使用します。</summary>
    /// <returns>既存の不変構成です。</returns>
    internal ScriptPackageEnvironment? Find(string revision)
    {
        if (!ScriptPackageRules.Revision(revision))
        {
            throw new ArgumentException("script.packages.invalidRevision", nameof(revision));
        }
        return ScriptRecordFile.Read<ScriptPackageEnvironment>(Path.Combine(root, "environments", revision + ".json"));
    }

    /// <summary>明示した同じ構成が残る場合は再導入せず、以前の依存解決結果を再使用します。</summary>
    /// <returns>実資産を確認した既存の環境です。</returns>
    internal ScriptPackageEnvironment? FindRequested(EScriptPackageKind kind, IReadOnlyList<ScriptPackageReference> requested, string? pythonPath, string? pythonVersion,
        string? nodePath = null, string? nodeVersion = null)
    {
        using (gate.EnterScope())
        {
            ScriptPackageEnvironment? existing = Directory.EnumerateFiles(Path.Combine(root, "environments"), "*.json")
                .Select(ScriptRecordFile.Read<ScriptPackageEnvironment>).OfType<ScriptPackageEnvironment>()
                .Where(environment => environment.Info is { } info && info.Kind == kind && info.Requested.SequenceEqual(requested)
                    && string.Equals(info.PythonPath, pythonPath, StringComparison.Ordinal) && string.Equals(info.PythonVersion, pythonVersion, StringComparison.Ordinal)
                    && string.Equals(info.NodePath, nodePath, StringComparison.Ordinal) && string.Equals(info.NodeVersion, nodeVersion, StringComparison.Ordinal))
                .MaxBy(environment => environment.Info!.CreatedAt);
            if (existing is null)
            {
                return null;
            }
            return Resolve(kind, existing.Info!.Revision);
        }
    }

    /// <summary>構成の永続化を先に完了し、最後に現在構成を原子的に切り替えます。</summary>
    internal void Publish(ScriptPackageEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment.Info);
        using (gate.EnterScope())
        {
            string revision = environment.Info.Revision;
            if (Find(revision) is null)
            {
                ScriptRecordFile.Write(Path.Combine(root, "environments", revision + ".json"), environment);
            }
            ScriptRecordFile.Write(CurrentPath(environment.Info.Kind), environment.Info);
        }
    }

    /// <summary>同じ識別子の再送を別のインストールへ変換しません。</summary>
    /// <returns>永続化済みの受付または結果です。</returns>
    public ScriptPackageOperation? Operation(Guid id)
    {
        using (gate.EnterScope())
        {
            return ScriptRecordFile.Read<ScriptPackageOperation>(Path.Combine(root, "operations", id.ToString("N") + ".json"));
        }
    }

    /// <summary>再起動時の中断判定と画面の結果照会に使用します。</summary>
    /// <returns>新しい順に並べた操作記録です。</returns>
    internal IReadOnlyList<ScriptPackageOperation> Operations()
    {
        using (gate.EnterScope())
        {
            return Directory.EnumerateFiles(Path.Combine(root, "operations"), "*.json")
                .Select(ScriptRecordFile.Read<ScriptPackageOperation>).OfType<ScriptPackageOperation>().OrderByDescending(operation => operation.UpdatedAt).ToArray();
        }
    }

    /// <summary>受付と結果を、サービス停止を跨いで保持します。</summary>
    internal void Save(ScriptPackageOperation operation)
    {
        using (gate.EnterScope())
        {
            ScriptRecordFile.Write(Path.Combine(root, "operations", operation.Request.OperationId.ToString("N") + ".json"), operation);
        }
    }

    /// <summary>配布方式ごとの現在構成を混在させません。</summary>
    /// <returns>固定されたファイル名です。</returns>
    private string CurrentPath(EScriptPackageKind kind) => Path.Combine(root, kind + "-current.json");

}
