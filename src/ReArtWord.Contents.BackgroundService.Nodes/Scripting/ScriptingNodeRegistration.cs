using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>各言語を同じ実行・結果・取消の通常ノードへ登録します。</summary>
public static class ScriptingNodeRegistration
{
    /// <summary>明示Coreワーカーを使う自由記述ノードです。</summary>
    /// <returns>C#・Python・JavaScriptの実行ノードです。</returns>
    public static IReadOnlyList<INode> Create(ScriptHostCommand host, ScriptPackageStore? packages = null, ScriptRuntimeRegistry? runtimes = null)
        => [new ScriptNode("csharp", host, packages, runtimes), new CSharpSourceNode(host, packages), new ScriptNode("python", host, packages, runtimes), new ScriptNode("javascript", host, packages, runtimes)];
}
