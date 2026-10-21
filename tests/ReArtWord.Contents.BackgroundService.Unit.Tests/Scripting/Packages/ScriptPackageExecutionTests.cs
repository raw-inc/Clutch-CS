using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>パッケージの実復元・import・推移依存と、保存済みWFの版固定を検査します。</summary>
public sealed class ScriptPackageExecutionTests
{
    /// <summary>コンパイルで選んだ.NET標準参照を、古いパッケージ実装で実行時に置換しません。</summary>
    /// <returns>標準DLLと配布DLLが重なる場合の実プロセス確認です。</returns>
    [Fact]
    public async Task OlderPackageAssemblyDoesNotDowngradeFrameworkAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.CreateFrameworkOverlap();
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "1.0.0").ConfigureAwait(true)).Outcome);
        string settings = await fixture.SettingsAsync("csharp", "using System.Text.Json;\npublic static string Run() => JsonSerializer.Serialize(new[] { 1, 2 });").ConfigureAwait(true);
        Assert.Equal("[1,2]", (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true)).Outputs["value"].GetString());
    }

    /// <summary>製品と同名のDLLでも、解析で確認したパッケージ版を実プロセスが使用します。</summary>
    /// <returns>using・推移依存・更新・削除後の旧版実行の確認です。</returns>
    [Fact]
    public async Task NugetDependenciesAndSavedEnvironmentsRemainExecutableAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.CreateNuget("1.0.0", "first");
        fixture.CreateNuget("2.0.0", "second");
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "1.0.0").ConfigureAwait(true)).Outcome);
        Assert.Equal(2, fixture.Store.Current(EScriptPackageKind.Nuget)!.Installed.Count);
        string code = "using PackageEntry;\nusing Alias = PackageEntry.Entry;\npublic static string Run() => Alias.Read();";
        string first = await fixture.SettingsAsync("csharp", code).ConfigureAwait(true);
        Assert.Equal("first", (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "2.0.0").ConfigureAwait(true)).Outcome);
        string second = await fixture.SettingsAsync("csharp", code).ConfigureAwait(true);
        Assert.Equal("second", (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(second)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal("first", (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "2.0.0", remove: true).ConfigureAwait(true)).Outcome);
        Assert.Empty(fixture.Store.Current(EScriptPackageKind.Nuget)!.Requested);
        Assert.Equal("second", (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(second)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.NotEmpty(CSharpScriptCompiler.Analyze(code).Diagnostics);
        ScriptPackageStore restarted = new(fixture.DirectoryPath);
        ScriptAnalysis saved = JsonSerializer.Deserialize<JsonElement>(first).GetProperty("analysis").Deserialize<ScriptAnalysis>(ContractJson.Options)!;
        Assert.NotEmpty(restarted.Resolve(EScriptPackageKind.Nuget, saved.PackageEnvironmentId).References);
    }

    /// <summary>pipの隔離環境でimportを解決し、更新しても保存済みの旧環境を使用します。</summary>
    /// <returns>実Pythonによる推移依存・版固定とAST限定解析です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_PYTHON")]
    public async Task PipImportsUsePinnedVirtualEnvironmentAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.CreateWheels("1.0.0", "first");
        fixture.CreateWheels("2.0.0", "second");
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Pip, "1.0.0").ConfigureAwait(true)).Outcome);
        string code = "from contents_test_entry import read\ndef run() -> str:\n    return read()";
        string first = await fixture.SettingsAsync("python", code).ConfigureAwait(true);
        Assert.Equal("first", (await fixture.Node("python").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        ScriptAnalysisRequest unsafeImport = new("python", "import does_not_exist\nraise RuntimeError('must not execute')\ndef run() -> str:\n    return 'safe'");
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(unsafeImport, fixture.Store.ForAnalysis(unsafeImport)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        Assert.NotNull(analysis.PackageEnvironmentId);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Pip, "2.0.0").ConfigureAwait(true)).Outcome);
        string second = await fixture.SettingsAsync("python", code).ConfigureAwait(true);
        Assert.Equal("second", (await fixture.Node("python").ExecuteAsync(NodeTestContext.Create(second)).ConfigureAwait(true)).Outputs["value"].GetString());
        Assert.Equal("first", (await fixture.Node("python").ExecuteAsync(NodeTestContext.Create(first)).ConfigureAwait(true)).Outputs["value"].GetString());
        string revision = fixture.Store.Current(EScriptPackageKind.Pip)!.Revision;
        ScriptPackageOperation failed = await fixture.ChangeAsync(EScriptPackageKind.Pip, "99.0.0").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, failed.Outcome);
        Assert.Equal("script.packages.pipInstallFailed", failed.Reason);
        Assert.Equal(revision, fixture.Store.Current(EScriptPackageKind.Pip)!.Revision);
        int builds = Directory.GetDirectories(Path.Combine(fixture.DirectoryPath, "script-packages", "builds")).Length;
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Pip, "1.0.0").ConfigureAwait(true)).Outcome);
        Assert.Equal(builds, Directory.GetDirectories(Path.Combine(fixture.DirectoryPath, "script-packages", "builds")).Length);
        Assert.Equal("first", (await fixture.Node("python").ExecuteAsync(NodeTestContext.Create(await fixture.SettingsAsync("python", code).ConfigureAwait(true))).ConfigureAwait(true)).Outputs["value"].GetString());
    }

    /// <summary>再起動前の未完了要求を自動実行せず、元のIDに結果不明を残します。</summary>
    /// <returns>導入要求の永続化と再起動境界です。</returns>
    [Fact]
    public async Task RestartMarksPendingOperationUnknownWithoutRetryAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-packages-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            ScriptPackageStore store = new(directory);
            ScriptPackageChangeRequest request = new(Guid.NewGuid(), EScriptPackageKind.Nuget, new("unavailable.example", "1.0.0"));
            ScriptPackageOperation pending = new(request, ContractJson.Hash(request), "installing", EOutcome.Unknown, false, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(directory, "script-packages", "operations", request.OperationId.ToString("N") + ".json"),
                JsonSerializer.Serialize(pending, ContractJson.Options)).ConfigureAwait(true);
            ScriptPackageManager manager = new(store);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = manager.ConfigureAwait(true);
            ScriptPackageOperation resumed = manager.Change(request);
            Assert.True(resumed.Completed);
            Assert.Equal("interrupted", resumed.Phase);
            Assert.Equal(EOutcome.Unknown, resumed.Outcome);
            Assert.False(manager.Catalog().Busy);
            Assert.Null(manager.Catalog().Nuget);
            Assert.False(Directory.Exists(Path.Combine(directory, "script-packages", "builds")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>導入失敗・競合・同じIDの再送で、現在構成を変えたり再実行したりしません。</summary>
    /// <returns>管理操作の障害境界です。</returns>
    [Fact]
    public async Task FailedInstallAndDuplicateRequestsPreserveCurrentEnvironmentAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.CreateNuget("1.0.0", "first");
        ScriptPackageOperation original = await fixture.ChangeAsync(EScriptPackageKind.Nuget, "1.0.0").ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, original.Outcome);
        Assert.Equal(original, fixture.Manager.Change(original.Request));
        Assert.Equal("script.packages.operationMismatch", fixture.Manager.Change(original.Request with { Remove = true }).Reason);
        ScriptPackageOperation failed = await fixture.ChangeAsync(EScriptPackageKind.Nuget, "99.0.0").ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, failed.Outcome);
        Assert.Equal(original.Revision, fixture.Store.Current(EScriptPackageKind.Nuget)!.Revision);
        ScriptPackageOperation stale = fixture.Manager.Change(original.Request with { OperationId = Guid.NewGuid() });
        Assert.Equal(EOutcome.NotExecuted, stale.Outcome);
        Assert.Equal("script.packages.configurationChanged", stale.Reason);
        Assert.Throws<ArgumentException>(() => fixture.Manager.Change(new(Guid.NewGuid(), EScriptPackageKind.Pip, new("--index-url", "1.0"))));
        Assert.Throws<ArgumentException>(() => fixture.Manager.Change(new(Guid.NewGuid(), EScriptPackageKind.Nuget, new("contents.test.entry", "*"))));
    }

    /// <summary>未導入Peerで版を代替せず、ユーザーコード開始前に拒否します。</summary>
    /// <returns>旧WFの標準環境互換と固定構成欠落の未実行です。</returns>
    [Fact]
    public async Task MissingPinnedEnvironmentDoesNotFallBackToCurrentPackagesAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        string code = "public static string Run() => \"legacy\";";
        ScriptAnalysis legacy = CSharpScriptCompiler.Analyze(code);
        string settings = JsonSerializer.Serialize(new { code, analysis = legacy }, ContractJson.Options);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true)).Outcome);
        settings = JsonSerializer.Serialize(new { code, analysis = legacy with { PackageEnvironmentId = new string('a', 64) } }, ContractJson.Options);
        Assert.Equal(EOutcome.NotExecuted, (await fixture.Node("csharp").ExecuteAsync(NodeTestContext.Create(settings)).ConfigureAwait(true)).Outcome);
    }
}
