using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>導入操作の失敗・再送・停止と公式実行環境の実インストールを検証します。</summary>
public sealed class ScriptRuntimeInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "contents-runtime-install-" + Guid.NewGuid().ToString("N"));

    /// <summary>取得に失敗しても以前の既定を維持し、同じ識別子でダウンロードを再実行しません。</summary>
    /// <returns>実HTTP境界から元操作の照会までの確認です。</returns>
    [RuntimeInstallFact("CONTENTS_TEST_NODE")]
    public async Task FailedInstallPreservesDefaultAndDuplicateRequest()
    {
        using ScriptRuntimeRegistry registry = new(root, () => new("test", "Test", Path.Combine(root, "user")));
        string original = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")!;
        ScriptRuntimeCatalog previous = await registry.ChangeAsync(new("javascript", original, true)).ConfigureAwait(true);
        using RuntimeInstallFaultHandler handler = new(false);
        using HttpClient http = new(handler, disposeHandler: false);
        ScriptRuntimeInstaller installer = new(root, registry, http);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = installer.ConfigureAwait(true);
        ScriptRuntimeInstallRequest request = new(Guid.NewGuid(), "javascript", "24.0.0");
        Assert.False(installer.Start(request).Completed);
        ScriptRuntimeInstallOperation failed = await WaitAsync(installer, request.OperationId).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, failed.Outcome);
        Assert.Equal("script.runtime.checksumMismatch", failed.Reason);
        Assert.False(failed.DefaultApplied);
        Assert.Equal(original, await registry.ResolveAsync("javascript", null).ConfigureAwait(true));
        Assert.Equal(previous.Revision, registry.Revision("javascript"));
        int calls = handler.Calls;
        Assert.Equal(failed, installer.Start(request));
        Assert.Equal(calls, handler.Calls);
        Assert.Equal("script.runtime.operationMismatch", installer.Start(request with { Version = "24.1.0" }).Reason);
        Assert.False(Directory.Exists(failed.Directory));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(failed.Directory!)!, ".install-*"));
    }

    /// <summary>Core停止は取得を取り消して元操作を中断扱いにし、再起動で再送しません。</summary>
    /// <returns>未完了処理の解放と元結果の永続化です。</returns>
    [RuntimeInstallFact]
    public async Task StopCancelsDownloadAndRestartKeepsOriginalOutcome()
    {
        using ScriptRuntimeRegistry registry = new(root, () => new("test", "Test", Path.Combine(root, "user")));
        using RuntimeInstallFaultHandler handler = new(true);
        using HttpClient http = new(handler, disposeHandler: false);
        ScriptRuntimeInstaller installer = new(root, registry, http);
        ScriptRuntimeInstallRequest request = new(Guid.NewGuid(), "javascript", "24.0.0");
        await using (installer.ConfigureAwait(true))
        {
            installer.Start(request);
            await handler.Downloading.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        Assert.Equal(EOutcome.Unknown, installer.Operation(request.OperationId)!.Outcome);
        using HttpClient secondHttp = new(handler, disposeHandler: false);
        ScriptRuntimeInstaller restarted = new(root, registry, secondHttp);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = restarted.ConfigureAwait(true);
        ScriptRuntimeInstallOperation result = restarted.Start(request);
        Assert.True(result.Completed);
        Assert.Equal("script.runtime.interrupted", result.Reason);
        Assert.Null(registry.Revision("javascript"));
    }

    /// <summary>公式フルZIPを隔離ホームへ導入し、venv・pipとnpmを同じ製品経路で検証します。</summary>
    /// <returns>ネットワークを明示許可した環境だけで行う実配布物の結合確認です。</returns>
    [RuntimeInstallFact("CONTENTS_TEST_RUNTIME_INSTALL")]
    public async Task OfficialPythonAndNodeInstallIntoIsolatedLoggedInHome()
    {
        string home = Path.Combine(root, "logged-in-user");
        using ScriptRuntimeRegistry registry = new(root, () => new("test", "Test", home));
        ScriptRuntimeInstaller installer = new(root, registry);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = installer.ConfigureAwait(true);
        foreach (string language in new[] { "python", "javascript" })
        {
            IReadOnlyList<ScriptRuntimeRelease> choices = await installer.ReleasesAsync(language).ConfigureAwait(true);
            Assert.NotEmpty(choices);
            ScriptRuntimeInstallRequest request = new(Guid.NewGuid(), language, choices[0].Version);
            installer.Start(request);
            ScriptRuntimeInstallOperation operation = await WaitAsync(installer, request.OperationId).ConfigureAwait(true);
            Assert.True(operation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(operation));
            Assert.True(operation.DefaultApplied);
            Assert.StartsWith(home + Path.DirectorySeparatorChar, operation.ExecutablePath, StringComparison.Ordinal);
            Assert.Equal(operation.ExecutablePath, await registry.ResolveAsync(language, null).ConfigureAwait(true));
            Assert.True(File.Exists(operation.ExecutablePath));
            Assert.Equal(operation, installer.Start(request));
            ScriptPackageStore packages = new(Path.Combine(root, language));
            ScriptPackageManager manager = new(packages, runtimes: registry);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable packageLifetime = manager.ConfigureAwait(true);
            EScriptPackageKind kind = EScriptPackageKind.Npm;
            string code = "export function run() { return 'verified'; }";
            string python = "python";
            if (string.Equals(language, python, StringComparison.Ordinal))
            {
                kind = EScriptPackageKind.Pip;
                code = "def run() -> str:\n    return 'verified'";
            }
            ScriptPackageChangeRequest change = new(Guid.NewGuid(), kind, new("unused", string.Empty), Remove: true);
            Assert.False(manager.Change(change).Completed);
            using CancellationTokenSource limit = new(TimeSpan.FromMinutes(2));
            while (packages.Operation(change.OperationId) is { Completed: false })
            {
                await Task.Delay(50, limit.Token).ConfigureAwait(true);
            }
            Assert.Equal(EOutcome.Succeeded, packages.Operation(change.OperationId)!.Outcome);
            ScriptAnalysisRequest analysisRequest = new(language, code);
            ScriptPackageEnvironment environment = await registry.ForAnalysisAsync(analysisRequest, packages, limit.Token).ConfigureAwait(true);
            ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(analysisRequest, environment, limit.Token).ConfigureAwait(true);
            Assert.Empty(analysis.Diagnostics);
            Assert.NotNull(analysis.Contract);
            Assert.Equal(packages.Current(kind)!.Revision, analysis.PackageEnvironmentId);
        }
    }

    /// <summary>所有する導入処理の終了後に、一時ファイルを回収します。</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            TestProcessDirectory.Delete(root);
        }
    }

    /// <summary>元の識別子を照会し、副作用を再送せず完了を待ちます。</summary>
    /// <returns>永続化済みの最終結果です。</returns>
    private static async Task<ScriptRuntimeInstallOperation> WaitAsync(ScriptRuntimeInstaller installer, Guid id, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        ScriptRuntimeInstallOperation operation = installer.Operation(id)!;
        while (!operation.Completed || installer.Status().Busy)
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            operation = installer.Operation(id)!;
        }
        return operation;
    }
}

/// <summary>公式URLへの要求形式を保ったまま、不一致または中断を再現します。</summary>
internal sealed class RuntimeInstallFaultHandler(bool block) : HttpMessageHandler
{
    internal int Calls
    {
        get; private set;
    }

    internal TaskCompletionSource Downloading
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>一覧とハッシュは返し、ZIP受信でだけ指定した障害を発生させます。</summary>
    /// <returns>模擬した有限配布情報です。</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        Calls++;
        string content = "incorrect binary", index = "index.json", hashes = "SHASUMS256.txt";
        if (request.RequestUri!.AbsolutePath.EndsWith(index, StringComparison.Ordinal))
        {
            content = "[{\"version\":\"v24.0.0\",\"lts\":\"test\",\"files\":[\"win-x64-zip\",\"win-arm64-zip\"]}]";
        }
        else
        {
            if (request.RequestUri.AbsolutePath.EndsWith(hashes, StringComparison.Ordinal))
            {
                content = new string('a', 64) + "  node-v24.0.0-win-x64.zip\n" + new string('a', 64) + "  node-v24.0.0-win-arm64.zip";
            }
            else
            {
                Downloading.TrySetResult();
                if (block)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
