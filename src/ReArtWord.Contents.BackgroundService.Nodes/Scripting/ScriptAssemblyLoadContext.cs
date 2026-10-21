using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>製品が使うDLLと利用者パッケージの版を、ワーカープロセス内でも分けます。</summary>
internal sealed class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyList<string> assemblies;
    private readonly IReadOnlyList<string> nativeLibraries;

    /// <summary>復元で確定した実行時資産だけを探索対象とします。</summary>
    internal ScriptAssemblyLoadContext(IReadOnlyList<string> assemblies, IReadOnlyList<string> nativeLibraries)
        : base("WorkflowScript", isCollectible: false)
    {
        this.assemblies = assemblies;
        this.nativeLibraries = nativeLibraries;
    }

    /// <summary>パッケージの実装DLLを優先し、標準ライブラリーは既定ロードへ委ねます。</summary>
    /// <returns>要求された文化・名前に対応するDLLです。</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        foreach (string path in assemblies)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            AssemblyName candidate = AssemblyName.GetAssemblyName(path);
            if (string.Equals(candidate.CultureName ?? string.Empty, assemblyName.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                if (candidate.Version < assemblyName.Version)
                {
                    return null;
                }
                return LoadFromAssemblyPath(path);
            }
        }
        return null;
    }

    /// <summary>RID別に選択されたネイティブライブラリーを、一般的なOSの名前解決規則で探します。</summary>
    /// <returns>明示資産から取得したハンドル、または標準探索への委譲です。</returns>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string name = Path.GetFileName(unmanagedDllName);
        string[] candidates = [name, name + ".dll", name + ".so", "lib" + name + ".so", name + ".dylib", "lib" + name + ".dylib"];
        foreach (string path in nativeLibraries)
        {
            if (candidates.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                return NativeLibrary.Load(path);
            }
        }
        return nint.Zero;
    }
}
