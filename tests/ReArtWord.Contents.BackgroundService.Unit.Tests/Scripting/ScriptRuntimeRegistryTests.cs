using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>実ランタイムで、既定選択・保存互換性・一回の解析と実行の一貫性を検証します。</summary>
public sealed class ScriptRuntimeRegistryTests
{
    /// <summary>Pythonのパス未指定でも元の解析指紋と型契約を維持して実行します。</summary>
    /// <returns>実Python子プロセスの確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public Task PythonDefaultSupportsAnalysisAndExecution() => DefaultAsync("python", "CONTENTS_TEST_PYTHON", "def run() -> str:\n    return 'runtime-selected'");

    /// <summary>Node.jsでも同じ既定選択と解析指紋の契約を守ります。</summary>
    /// <returns>実Node.js子プロセスの確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public Task NodeDefaultSupportsAnalysisAndExecution() => DefaultAsync("javascript", "CONTENTS_TEST_NODE", "export function run() { return 'runtime-selected'; }");

    /// <summary>登録済み既定が消えた場合に、PATH上の別の版へ暗黙で切り替えません。</summary>
    /// <returns>永続化・競合検査・明示パス優先の確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task MissingDefaultDoesNotFallBackAndStaleWritesAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")!;
        string copy = Path.Combine(root, Path.GetFileName(original));
        File.Copy(original, copy);
        try
        {
            using ScriptRuntimeRegistry registry = new(root, () => new("test", "Test", root));
            ScriptRuntimeCatalog first = await registry.ChangeAsync(new("javascript", copy, true)).ConfigureAwait(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ChangeAsync(new("javascript", original, true))).ConfigureAwait(true);
            Assert.Equal(copy, await registry.ResolveAsync("javascript", null).ConfigureAwait(true));
            Assert.Equal(original, await registry.ResolveAsync("javascript", original).ConfigureAwait(true));
            File.Delete(copy);
            using ScriptRuntimeRegistry restarted = new(root, () => new("test", "Test", root));
            Assert.Equal(first.Revision, restarted.Revision("javascript"));
            InvalidOperationException missing = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ResolveAsync("javascript", null, original)).ConfigureAwait(true);
            Assert.Equal("script.runtime.unavailable", missing.Message);
            ScriptRuntimeCatalog cleared = await restarted.ChangeAsync(new("javascript", null, true, first.Revision)).ConfigureAwait(true);
            Assert.Null(cleared.DefaultPath);
            Assert.Contains(cleared.Entries, entry => string.Equals(entry.Path, copy, StringComparison.Ordinal) && entry.Registered && !entry.Available);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>未ログインでもPATHの検出は許可し、ユーザーホームへの導入だけを不可として示します。</summary>
    /// <returns>候補と導入権限を混同しない確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task UserAbsenceDoesNotDisableExplicitRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ScriptRuntimeRegistry registry = new(root, () => throw new InvalidOperationException("not logged in"));
            string path = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")!;
            ScriptRuntimeCatalog catalog = await registry.ChangeAsync(new("javascript", path, true)).ConfigureAwait(true);
            Assert.Null(catalog.InstallDirectory);
            Assert.Equal("script.runtime.userUnavailable", catalog.InstallProblem);
            Assert.Equal(path, await registry.ResolveAsync("javascript", null).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Node描画の候補照会と、解析・実行の既定解決で同じ設定を使います。</summary>
    /// <returns>対応言語の実行結果です。</returns>
    private static async Task DefaultAsync(string language, string variable, string code, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Environment.GetEnvironmentVariable(variable)!;
            using ScriptRuntimeRegistry registry = new(root, () => new("test", "Test", root, Path.GetDirectoryName(path)));
            ScriptRuntimeCatalog detected = await registry.CatalogAsync(new(language), cancellationToken).ConfigureAwait(false);
            Assert.Contains(detected.Entries, item => ScriptRuntimePaths.Comparer.Equals(item.Path, path) && item.Available);
            ScriptRuntimeCatalog selected = await registry.ChangeAsync(new(language, path, true), cancellationToken).ConfigureAwait(false);
            Assert.Equal(path, selected.DefaultPath);
            ScriptPackageStore packages = new(root);
            ScriptAnalysisRequest request = new(language, code);
            ScriptPackageEnvironment environment = await registry.ForAnalysisAsync(request, packages, cancellationToken).ConfigureAwait(false);
            ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(request, environment, cancellationToken).ConfigureAwait(false);
            Assert.Equal(ContractJson.Hash(request), analysis.Fingerprint);
            Assert.Empty(analysis.Diagnostics);
            Assert.NotNull(analysis.Contract);
            string settings = JsonSerializer.Serialize(new { code, analysis }, ContractJson.Options);
            IActionNode node = (IActionNode)ScriptingNodeRegistration.Create(new(Environment.ProcessPath!, []), packages, registry).Single(item => string.Equals(item.Descriptor.Type, "script." + language, StringComparison.Ordinal));
            NodeResult result = await node.ExecuteAsync(NodeTestContext.Create(settings), cancellationToken).ConfigureAwait(false);
            Assert.Equal(EOutcome.Succeeded, result.Outcome);
            Assert.Equal("runtime-selected", result.Outputs["value"].GetString());
            Assert.Equal(environment.RuntimeExecutable, (await registry.BindAsync(request, environment, cancellationToken).ConfigureAwait(false)).RuntimeExecutable);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
