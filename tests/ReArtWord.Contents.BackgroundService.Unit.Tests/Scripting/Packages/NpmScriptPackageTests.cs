using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>実npmの導入と、ESM・CommonJS・固定構成を使うNode.js実行を検査します。</summary>
public sealed class NpmScriptPackageTests
{
    /// <summary>scoped・subpath・推移依存を扱い、更新・削除後も保存済みの環境で実行します。</summary>
    /// <returns>実HTTP・npm CLI・Node.jsワーカーによる確認です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task NpmImportsKeepTheirSavedDependencyGraphAsync()
    {
        NpmRegistryFixture registry = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable registryLifetime = registry.ConfigureAwait(true);
        ScriptPackageFixture fixture = new(registry.Address);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        ScriptPackageOperation installed = await fixture.ChangeAsync(EScriptPackageKind.Npm, "1.0.0").ConfigureAwait(true);
        Assert.True(installed.Outcome == EOutcome.Succeeded, installed.Reason);
        Assert.Equal(2, fixture.Store.Current(EScriptPackageKind.Npm)!.Installed.Count);
        string code = "import { read } from '@contents/entry'; import suffix from '@contents/entry/extra'; export async function run() { return read() + suffix; }";
        JsonElement manual = JsonSerializer.SerializeToElement(new { arguments = new { }, result = new { type = "string" } });
        string first = await fixture.SettingsAsync("javascript", code, manual).ConfigureAwait(true);
        Assert.Equal("first!", (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Npm, "2.0.0").ConfigureAwait(true)).Outcome);
        string second = await fixture.SettingsAsync("javascript", code, manual).ConfigureAwait(true);
        Assert.Equal("second!", (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(second)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal("first!", (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        string revision = fixture.Store.Current(EScriptPackageKind.Npm)!.Revision;
        ScriptPackageOperation failed = await fixture.ChangeAsync(EScriptPackageKind.Npm, "99.0.0").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, failed.Outcome);
        Assert.Equal("script.packages.npmResolveFailed", failed.Reason);
        Assert.Equal(revision, fixture.Store.Current(EScriptPackageKind.Npm)!.Revision);
        Assert.Equal(installed, fixture.Manager.Change(installed.Request));
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Npm, "2.0.0", remove: true).ConfigureAwait(true)).Outcome);
        Assert.Empty(fixture.Store.Current(EScriptPackageKind.Npm)!.Requested);
        Assert.Equal("second!", (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(second)).ConfigureAwait(true)).Outputs["value"].GetString());
        string missing = await fixture.SettingsAsync("javascript", code, manual).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(missing)).ConfigureAwait(true)).Outcome);
        string outside = Path.Combine(fixture.DirectoryPath, "node_modules", "outside-package");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "package.json"), "{\"name\":\"outside-package\",\"version\":\"1.0.0\",\"type\":\"module\",\"main\":\"index.js\"}").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(outside, "index.js"), "export default 'must-not-load';").ConfigureAwait(true);
        string external = await fixture.SettingsAsync("javascript", "import value from 'outside-package'; export function run() { return value; }", manual).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, (await fixture.Node("javascript").ExecuteAsync(NodeTestContext.Create(external)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>npmの別名・URL・範囲指定を固定版要求として誤って受理しません。</summary>
    /// <returns>副作用開始前の拒否です。</returns>
    [Fact]
    public async Task NpmRequiresRegistryNamesAndExactVersionsAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        foreach (string version in new[] { "latest", "^1.0.0", "~1.0.0", "file:archive.tgz", "npm:other@1.0.0", "1.0.0 --global" })
        {
            Assert.Throws<ArgumentException>(() => fixture.Manager.Change(new(Guid.NewGuid(), EScriptPackageKind.Npm, new("@scope/name", version))));
        }
        Assert.Throws<ArgumentException>(() => fixture.Manager.Change(new(Guid.NewGuid(), EScriptPackageKind.Npm, new("../../target", "1.0.0"))));
        ScriptPackageOperation missingRuntime = fixture.Manager.Change(new(Guid.NewGuid(), EScriptPackageKind.Npm, new("@scope/name", "1.0.0")));
        Assert.Equal(EOutcome.NotExecuted, missingRuntime.Outcome);
        Assert.Equal("script.packages.nodePathRequired", missingRuntime.Reason);
    }

    /// <summary>新しい省略可能項目で、保存済みC#・Pythonの解析指紋を変えません。</summary>
    [Fact]
    public void OldAnalysisRequestsKeepTheirFingerprints()
    {
        ScriptAnalysisRequest request = new("csharp", "public static int Run() => 1;");
        Assert.Equal(ContractJson.Hash(new { language = request.Language, code = request.Code, pythonPath = (string?)null }), ContractJson.Hash(request));
    }
}
