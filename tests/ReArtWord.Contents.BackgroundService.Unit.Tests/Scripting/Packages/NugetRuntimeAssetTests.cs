using System.Runtime.InteropServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>対象RIDの限定、フォールバック、OS限定依存と版競合の境界を実NuGetで検証します。</summary>
public sealed class NugetRuntimeAssetTests
{
    /// <summary>フォールバックRIDを復元対象へ増やさず、現在OSのネイティブ資産だけを選択します。</summary>
    /// <returns>実ローカルフィードの復元結果です。</returns>
    [Fact]
    public async Task RuntimeGraphSelectsFallbackAssetsWithoutRestoringOtherPlatformsAsync()
    {
        using NugetAssetFixture fixture = new();
        string runtime = RuntimeInformation.RuntimeIdentifier;
        string fallback = "unix";
        string other = "win-x64";
        if (OperatingSystem.IsWindows())
        {
            fallback = "win";
            other = "linux-x64";
        }
        fixture.Package("contents.test.fallback", "1.0.0", ["lib/net10.0/Fallback.dll", "runtimes/" + fallback + "/native/Fallback.bin", "runtimes/" + other + "/native/Other.bin"]);
        fixture.Package("contents.test.native", "1.0.0", ["lib/net10.0/Managed.dll", "runtimes/" + runtime + "/native/Exact.bin", "runtimes/" + other + "/native/Other.bin"], "contents.test.fallback", "[1.0.0]");
        ScriptPackageEnvironment environment = await fixture.RestoreAsync([new("contents.test.native", "1.0.0")]).ConfigureAwait(true);
        Assert.Equal(2, environment.NativeLibraries.Count);
        Assert.Contains(environment.NativeLibraries, path => string.Equals(File.ReadAllText(path), "runtimes/" + fallback + "/native/Fallback.bin", StringComparison.Ordinal));
        Assert.Contains(environment.NativeLibraries, path => string.Equals(File.ReadAllText(path), "runtimes/" + runtime + "/native/Exact.bin", StringComparison.Ordinal));
        using JsonDocument assets = fixture.Assets();
        Assert.Equal(["net10.0", "net10.0/" + runtime], assets.RootElement.GetProperty("targets").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>別OSでだけ呼び出す推移依存は参照を維持し、実行資産へ混入させません。</summary>
    /// <returns>PackageReferenceと同じOS限定依存の扱いです。</returns>
    [Fact]
    public async Task PlatformSpecificTransitiveDependencyPreservesCompileReferenceAsync()
    {
        using NugetAssetFixture fixture = new();
        string other = "win-x64";
        if (OperatingSystem.IsWindows())
        {
            other = "linux-x64";
        }
        fixture.Package("contents.test.platform", "1.0.0", ["ref/net10.0/Platform.dll", "runtimes/" + other + "/lib/net10.0/Platform.dll"]);
        fixture.Package("contents.test.entry", "1.0.0", ["lib/net10.0/Entry.dll"], "contents.test.platform", "[1.0.0]");
        ScriptPackageEnvironment environment = await fixture.RestoreAsync([new("contents.test.entry", "1.0.0")]).ConfigureAwait(true);
        Assert.Equal(2, environment.Info!.Installed.Count);
        Assert.Contains(environment.References, path => string.Equals(Path.GetFileName(path), "Platform.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(environment.Assemblies, path => string.Equals(Path.GetFileName(path), "Platform.dll", StringComparison.Ordinal));
        Assert.Single(environment.Assemblies);
    }

    /// <summary>OS資産の検査方針を揃えても、推移依存のダウングレード・範囲外選択は失敗させます。</summary>
    /// <returns>NuGetが警告として返す版競合も拒否する確認です。</returns>
    [Theory]
    [InlineData("[2.0.0,)", "1.0.0", "NU1605")]
    [InlineData("[1.0.0]", "2.0.0", "NU1608")]
    public async Task VersionConflictsStillFailAsync(string range, string directVersion, string code)
    {
        using NugetAssetFixture fixture = new();
        fixture.Package("contents.test.dependency", "1.0.0", ["lib/net10.0/Dependency.dll"]);
        fixture.Package("contents.test.dependency", "2.0.0", ["lib/net10.0/Dependency.dll"]);
        fixture.Package("contents.test.entry", "1.0.0", ["lib/net10.0/Entry.dll"], "contents.test.dependency", range);
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RestoreAsync([new("contents.test.entry", "1.0.0"), new("contents.test.dependency", directVersion)])).ConfigureAwait(true);
        Assert.Contains(code, failure.Message, StringComparison.Ordinal);
    }
}
