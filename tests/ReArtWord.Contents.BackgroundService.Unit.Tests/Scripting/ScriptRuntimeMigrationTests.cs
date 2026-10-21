using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Runtimes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Database;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>管理済み旧配置の移行と、保存済みWF・パッケージ環境の互換性を検証します。</summary>
public sealed class ScriptRuntimeMigrationTests
{
    /// <summary>両言語の現在選択と旧明示パスを新配置へ移し、再起動しても対応を維持します。</summary>
    /// <returns>全内容、空ディレクトリ、既定、互換参照と冪等性の確認です。</returns>
    [Theory]
    [InlineData("python")]
    [InlineData("javascript")]
    public async Task ManagedRuntimeMovesSelectionWithoutBreakingPinnedPathsAsync(string language)
    {
        using MigrationFixture fixture = new(language);
        using ScriptRuntimeRegistry registry = new(fixture.Root, () => fixture.User);
        string? acceptedRevision = registry.Revision(language);
        ScriptRuntimeCatalog catalog = await registry.CatalogAsync(new(language, true)).ConfigureAwait(true);
        Assert.Null(catalog.InstallProblem);
        Assert.Equal(fixture.Current, catalog.DefaultPath);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Current)), catalog.InstallDirectory);
        Assert.Equal(fixture.Current, await registry.ResolveAsync(language, fixture.Previous).ConfigureAwait(true));
        Assert.Equal(await File.ReadAllBytesAsync(fixture.Previous).ConfigureAwait(true), await File.ReadAllBytesAsync(fixture.Current).ConfigureAwait(true));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.Current)!, "empty")));
        Assert.True(File.Exists(fixture.Previous));
        using ScriptRuntimeRegistry restarted = new(fixture.Root, () => fixture.User);
        Assert.Equal(fixture.Current, await restarted.ResolveAsync(language, null).ConfigureAwait(true));
        Assert.Equal(fixture.Current, await restarted.ResolveAsync(language, fixture.Previous).ConfigureAwait(true));
        ScriptRuntimeCatalog unchanged = await restarted.CatalogAsync(new(language, true)).ConfigureAwait(true);
        Assert.Equal(catalog.Revision, unchanged.Revision);
        Assert.DoesNotContain(unchanged.Entries, entry => ScriptRuntimePaths.Comparer.Equals(entry.Path, fixture.Previous));
        Assert.True(registry.CompleteInstall(language, fixture.Current, acceptedRevision));
    }

    /// <summary>異なる既存配置は上書きせず、元の既定で処理を継続できます。</summary>
    /// <returns>同一内容なら再使用し、異内容なら移行保留とする確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDestinationIsReusedOnlyWhenEveryEntryMatchesAsync(bool conflict)
    {
        using MigrationFixture fixture = new("javascript");
        string source = Path.GetDirectoryName(fixture.Previous)!, target = Path.GetDirectoryName(fixture.Current)!;
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "empty"));
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        if (conflict)
        {
            await File.WriteAllTextAsync(fixture.Current, "different installation").ConfigureAwait(true);
        }
        using ScriptRuntimeRegistry registry = new(fixture.Root, () => fixture.User);
        ScriptRuntimeCatalog catalog = await registry.CatalogAsync(new("javascript", true)).ConfigureAwait(true);
        if (conflict)
        {
            Assert.Equal("script.runtime.migrationPending", catalog.InstallProblem);
            Assert.Equal(fixture.Previous, catalog.DefaultPath);
            Assert.Equal("different installation", await File.ReadAllTextAsync(fixture.Current).ConfigureAwait(true));
        }
        else
        {
            Assert.Null(catalog.InstallProblem);
            Assert.Equal(fixture.Current, catalog.DefaultPath);
        }
        Assert.True(File.Exists(fixture.Previous));
    }

    /// <summary>旧Installerとの排他が取れない間は移行を保留し、解除後の明示更新で再開します。</summary>
    /// <returns>失敗時の既定維持と、失敗をキャッシュで固定しない確認です。</returns>
    [Fact]
    public async Task BusyInstallationPreservesDefaultAndCanRetryAsync()
    {
        using MigrationFixture fixture = new("python");
        using ScriptRuntimeRegistry registry = new(fixture.Root, () => fixture.User);
        string lockPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Previous))!, ".install.lock");
        using (FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            ScriptRuntimeCatalog busy = await registry.CatalogAsync(new("python")).ConfigureAwait(true);
            Assert.Equal("script.runtime.migrationPending", busy.InstallProblem);
            Assert.Equal(fixture.Previous, busy.DefaultPath);
            Assert.False(File.Exists(fixture.Current));
        }
        ScriptRuntimeCatalog resumed = await registry.CatalogAsync(new("python")).ConfigureAwait(true);
        Assert.Null(resumed.InstallProblem);
        Assert.Equal(fixture.Current, resumed.DefaultPath);
    }

    /// <summary>取消で不完全な配置や新しい既定を公開しません。</summary>
    /// <returns>元環境の維持と、別利用者ホームを移行しない確認です。</returns>
    [Fact]
    public async Task CancellationAndAnotherLoggedInUserLeaveThePreviousRuntimeIntactAsync()
    {
        using MigrationFixture fixture = new("python");
        using ScriptRuntimeRegistry registry = new(fixture.Root, () => fixture.User);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CatalogAsync(new("python", true), cancelled.Token)).ConfigureAwait(true);
        Assert.False(File.Exists(fixture.Current));
        using ScriptRuntimeRegistry otherUser = new(fixture.Root, () => new("other", "Other", Path.Combine(fixture.Root, "other")));
        Assert.Equal(fixture.Previous, (await otherUser.CatalogAsync(new("python", true)).ConfigureAwait(true)).DefaultPath);
        Assert.True(File.Exists(fixture.Previous));
        Assert.False(File.Exists(fixture.Current));
    }

    /// <summary>実Nodeを移行した後も、以前に固定したnpm環境を新しい解析へ継続して使えます。</summary>
    /// <returns>実行物の起動と、パッケージ環境の版・旧実行物の保持です。</returns>
    [ExternalRuntimeFact("CONTENTS_TEST_NODE")]
    public async Task MigratedNodeKeepsTheExistingPackageEnvironmentAsync()
    {
        using MigrationFixture fixture = new("javascript");
        File.Copy(Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE")!, fixture.Previous, true);
        ScriptPackageStore packages = new(fixture.Root);
        string npm = Path.Combine(fixture.Root, "npm");
        Directory.CreateDirectory(Path.Combine(npm, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(npm, "package-lock.json"), "{}").ConfigureAwait(true);
        string revision = new('a', 64);
        ScriptPackageEnvironmentInfo info = new(EScriptPackageKind.Npm, revision, [], [], DateTimeOffset.UtcNow, NodePath: fixture.Previous);
        packages.Publish(new(info, [], [], [], NpmDirectory: npm));
        using ScriptRuntimeRegistry registry = new(fixture.Root, () => fixture.User);
        Assert.Equal(fixture.Current, (await registry.CatalogAsync(new("javascript", true)).ConfigureAwait(true)).DefaultPath);
        ScriptPackageEnvironment environment = await registry.ForAnalysisAsync(new("javascript", "export function run() { return 1; }", NodePath: fixture.Current), packages).ConfigureAwait(true);
        Assert.Equal(revision, environment.Info!.Revision);
        Assert.Equal(fixture.Previous, environment.Info.NodePath);
        Assert.True(File.Exists(environment.Info.NodePath));
        Assert.Equal(fixture.Current, await registry.ResolveAsync("javascript", fixture.Previous).ConfigureAwait(true));
    }

    /// <summary>一つの隔離ホームと旧設定を所有し、本番の実行物を変更しません。</summary>
    private sealed class MigrationFixture : IDisposable
    {
        /// <summary>少量の管理済み配布物と既存の既定設定を作ります。</summary>
        internal MigrationFixture(string language)
        {
            Root = Path.Combine(Path.GetTempPath(), "contents-runtime-migration-" + Guid.NewGuid().ToString("N"));
            User = new("fixture", "Fixture", Path.Combine(Root, "home"));
            string leaf = language switch { "python" => "py", _ => "js" };
            string executable = language switch { "python" => "python.exe", _ => "node.exe" };
            string name = "24.0.0-win-x64";
            string directory = Path.Combine(User.HomeDirectory, ".clitch-cs", "runtime", leaf, name);
            Directory.CreateDirectory(Path.Combine(directory, "empty"));
            Previous = Path.Combine(directory, executable);
            Current = Path.Combine(User.HomeDirectory, ".clutch-cs", "runtime", leaf, name, executable);
            File.WriteAllText(Previous, "fixture executable");
            ScriptRecordFile.Write(Path.Combine(directory, ".clutch-runtime.json"), new ScriptRuntimeRelease(language, "24.0.0", "win-x64", true, new Uri("https://example.invalid/runtime.zip"), new string('a', 64)));
            Directory.CreateDirectory(Path.Combine(Root, "script-runtimes"));
            ScriptRecordFile.Write(Path.Combine(Root, "script-runtimes", language + ".json"), new ScriptRuntimeConfiguration("previous", [Previous], Previous));
        }

        internal string Root
        {
            get;
        }

        internal ScriptRuntimeUser User
        {
            get;
        }

        internal string Previous
        {
            get;
        }

        internal string Current
        {
            get;
        }

        /// <summary>このfixtureだけの一時領域を削除します。</summary>
        public void Dispose() => Directory.Delete(Root, true);
    }
}
