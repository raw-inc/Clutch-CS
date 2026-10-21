using System.IO.Compression;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>外部配布元に依存せず、実NuGet・pipと子プロセスを検証する配布物です。</summary>
internal sealed class ScriptPackageFixture : IAsyncDisposable
{
    internal string DirectoryPath
    {
        get;
    } = Path.Combine(Path.GetTempPath(), "contents-packages-" + Guid.NewGuid().ToString("N"));

    internal ScriptPackageStore Store
    {
        get;
    }

    internal ScriptPackageManager Manager
    {
        get;
    }

    /// <summary>パッケージキャッシュとPython環境を試験ごとに分離します。</summary>
    internal ScriptPackageFixture(string npmRegistry = "https://registry.npmjs.org", string? nugetSource = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        string source = Path.Combine(DirectoryPath, "feed");
        Directory.CreateDirectory(source);
        Store = new(DirectoryPath);
        Manager = new(Store, nugetSource ?? source, source, npmRegistry);
    }

    /// <summary>製品と同名のDLLを含め、推移依存の実装が版ごとに変わるNuGetを作ります。</summary>
    internal void CreateNuget(string version, string value)
    {
        byte[] dependency = Compile("PackageDependency", "namespace PackageDependency; public static class Value { public static string Text => \"" + value + "\"; }");
        byte[] entry = Compile("YamlDotNet", "namespace PackageEntry; public static class Entry { public static string Read() => PackageDependency.Value.Text; }", dependency);
        Nuget("contents.test.dependency", version, "PackageDependency", dependency, null);
        Nuget("contents.test.entry", version, "YamlDotNet", entry, version);
    }

    /// <summary>Pythonの推移依存とimportを検査する標準wheelを生成します。</summary>
    internal void CreateWheels(string version, string value)
    {
        Wheel("contents_test_dependency", version, "VALUE = \"" + value + "\"\n", null);
        Wheel("contents_test_entry", version, "from contents_test_dependency import VALUE\ndef read():\n    return VALUE\n", "contents-test-dependency==" + version);
    }

    /// <summary>.NET標準より古い同名DLLを含むパッケージを再現します。</summary>
    internal void CreateFrameworkOverlap()
    {
        byte[] assembly = Compile("System.Text.Json", "namespace Fixture; public static class OldFramework { public static string Value => \"old\"; }");
        Nuget("contents.test.entry", "1.0.0", "System.Text.Json", assembly, null);
    }

    /// <summary>受付後の同じ操作を照会し、最終結果まで待ちます。</summary>
    /// <returns>副作用の確定状態です。</returns>
    internal async Task<ScriptPackageOperation> ChangeAsync(EScriptPackageKind kind, string version, bool remove = false, CancellationToken cancellationToken = default)
    {
        string id = "contents.test.entry";
        string? python = null, node = null;
        if (kind == EScriptPackageKind.Pip)
        {
            id = "contents-test-entry";
            python = Environment.GetEnvironmentVariable("CONTENTS_TEST_PYTHON");
        }
        if (kind == EScriptPackageKind.Npm)
        {
            id = "@contents/entry";
            node = Environment.GetEnvironmentVariable("CONTENTS_TEST_NODE");
        }
        ScriptPackageChangeRequest request = new(Guid.NewGuid(), kind, new(id, version), remove, Store.Current(kind)?.Revision, python, NodePath: node);
        Manager.Change(request);
        return await WaitAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>インストールの完了を有限時間で確認します。</summary>
    /// <returns>元の操作の最終状態です。</returns>
    internal async Task<ScriptPackageOperation> WaitAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            ScriptPackageOperation result = Store.Operation(id)!;
            if (result.Completed)
            {
                return result;
            }
            await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("パッケージ操作が期限内に完了しませんでした。");
    }

    /// <summary>WorkflowEditorと同じ解析情報を通常ノードの設定へ含めます。</summary>
    /// <returns>保存・再読込みできるJSON設定です。</returns>
    internal async Task<string> SettingsAsync(string language, string code, JsonElement manual = default, CancellationToken cancellationToken = default)
    {
        ScriptAnalysisRequest request = new(language, code, manual);
        ScriptAnalysis report = await ScriptAnalysisService.AnalyzeAsync(request, Store.ForAnalysis(request), cancellationToken).ConfigureAwait(false);
        Assert.Empty(report.Diagnostics);
        Assert.NotNull(report.Contract);
        System.Text.Json.Nodes.JsonObject settings = JsonSerializer.SerializeToNode(new { code, analysis = report }, ContractJson.Options)!.AsObject();
        if (manual.ValueKind != JsonValueKind.Undefined)
        {
            settings["manualContract"] = JsonSerializer.SerializeToNode(manual, ContractJson.Options);
        }
        return settings.ToJsonString();
    }

    /// <summary>実Coreワーカーを起動する通常ノードです。</summary>
    /// <returns>保存先を共有する言語別ノードです。</returns>
    internal IActionNode Node(string language)
    {
        string executable = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            executable += ".exe";
        }
        return (IActionNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, executable), []), Store)
            .Single(node => string.Equals(node.Descriptor.Type, "script." + language, StringComparison.Ordinal));
    }

    /// <summary>全プロセスの停止確認後、今回作成した試験ディレクトリーだけを削除します。</summary>
    /// <returns>試験資源の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "所有する導入処理を停止する固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync().ConfigureAwait(false);
        Directory.Delete(DirectoryPath, true);
    }

    /// <summary>参照だけを渡して小さいライブラリーを生成し、コードは実行しません。</summary>
    /// <returns>配布用のDLLバイト列です。</returns>
    private static byte[] Compile(string name, string source, byte[]? dependency = null)
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        List<MetadataReference> references = trusted.Split(Path.PathSeparator).Where(path => string.Equals(Path.GetDirectoryName(path), runtime, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
        if (dependency is not null)
        {
            references.Add(MetadataReference.CreateFromImage(dependency));
        }
        CSharpCompilation compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references, new(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream output = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return output.ToArray();
    }

    /// <summary>ローカルフィードから実NuGet復元が読めるパッケージです。</summary>
    private void Nuget(string id, string version, string assemblyName, byte[] assembly, string? dependencyVersion)
    {
        using ZipArchive archive = ZipFile.Open(Path.Combine(DirectoryPath, "feed", id + "." + version + ".nupkg"), ZipArchiveMode.Create);
        string dependencies = string.Empty;
        if (dependencyVersion is not null)
        {
            dependencies = "<dependencies><dependency id=\"contents.test.dependency\" version=\"[" + dependencyVersion + "]\" /></dependencies>";
        }
        Write(archive, id + ".nuspec", "<?xml version=\"1.0\"?><package><metadata><id>" + id + "</id><version>" + version
            + "</version><authors>Tests</authors><description>Fixture</description>" + dependencies + "</metadata></package>");
        using Stream stream = archive.CreateEntry("lib/net10.0/" + assemblyName + ".dll").Open();
        stream.Write(assembly);
    }

    /// <summary>ビルドツールや通信なしでpipが導入する、純粋Pythonのwheelです。</summary>
    private void Wheel(string id, string version, string code, string? dependency)
    {
        using ZipArchive archive = ZipFile.Open(Path.Combine(DirectoryPath, "feed", id + "-" + version + "-py3-none-any.whl"), ZipArchiveMode.Create);
        string info = id + "-" + version + ".dist-info/";
        string metadata = "Metadata-Version: 2.1\nName: " + id + "\nVersion: " + version + "\n";
        if (dependency is not null)
        {
            metadata += "Requires-Dist: " + dependency + "\n";
        }
        Write(archive, id + ".py", code);
        Write(archive, info + "METADATA", metadata);
        Write(archive, info + "WHEEL", "Wheel-Version: 1.0\nGenerator: contents-tests\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
        Write(archive, info + "RECORD", id + ".py,,\n" + info + "METADATA,,\n" + info + "WHEEL,,\n" + info + "RECORD,,\n");
    }

    /// <summary>配布物内のテキストをUTF-8で保存します。</summary>
    private static void Write(ZipArchive archive, string path, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(path).Open(), new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}
