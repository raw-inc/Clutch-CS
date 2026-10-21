using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>継続スクリプトでも解析時に固定したNuGet構成を使うことを検証します。</summary>
public sealed class CSharpSourcePackageTests
{
    /// <summary>現在のパッケージを更新しても、保存した構成で複数要素を送出します。</summary>
    /// <returns>実パッケージ導入と実workerの版固定です。</returns>
    [Fact]
    public async Task SourceKeepsAnalyzedPackageVersionAsync()
    {
        ScriptPackageFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.CreateNuget("1.0.0", "first");
        fixture.CreateNuget("2.0.0", "second");
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "1.0.0").ConfigureAwait(true)).Outcome);
        const string code = "using PackageEntry; public static async IAsyncEnumerable<string> Run() { await Task.Yield(); yield return Entry.Read(); yield return Entry.Read(); }";
        ScriptAnalysisRequest request = new("csharp", code, Continuous: true);
        ScriptAnalysis analysis = await ScriptAnalysisService.AnalyzeAsync(request, fixture.Store.ForAnalysis(request)).ConfigureAwait(true);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAsync(EScriptPackageKind.Nuget, "2.0.0").ConfigureAwait(true)).Outcome);
        NodeExecutionContext initial = NodeTestContext.Create(JsonSerializer.Serialize(new { code, analysis }, ContractJson.Options));
        List<string?> values = [];
        NodeSourceContext source = new(initial, () => initial, (item, _) =>
        {
            values.Add(item.Result.Outputs["value"].GetString());
            return ValueTask.CompletedTask;
        }, () => false);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        NodeResult result = await CSharpSourceTests.Node(fixture.Store).RunAsync(source, deadline.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(["first", "first"], values);
    }
}
