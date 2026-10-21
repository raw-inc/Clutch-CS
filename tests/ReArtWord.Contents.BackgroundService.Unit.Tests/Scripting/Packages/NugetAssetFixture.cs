using System.IO.Compression;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting.Packages;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting.Packages;

/// <summary>RID別の資産と依存関係を持つ小さい配布物を、外部通信なしで復元します。</summary>
internal sealed class NugetAssetFixture : IDisposable
{
    internal string DirectoryPath
    {
        get;
    } = Path.Combine(Path.GetTempPath(), "contents-nuget-assets-" + Guid.NewGuid().ToString("N"));

    /// <summary>一回の復元に専用の出力とフィードを用意します。</summary>
    internal NugetAssetFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "feed"));
    }

    /// <summary>資産の選択を検証するため、各ファイルへ相対パスを識別子として保存します。</summary>
    internal void Package(string id, string version, IReadOnlyList<string> assets, string? dependency = null, string? range = null)
    {
        using ZipArchive archive = ZipFile.Open(Path.Combine(DirectoryPath, "feed", id + "." + version + ".nupkg"), ZipArchiveMode.Create);
        string dependencies = string.Empty;
        if (dependency is not null)
        {
            dependencies = "<dependencies><dependency id=\"" + dependency + "\" version=\"" + range + "\" /></dependencies>";
        }
        Write(archive, id + ".nuspec", "<?xml version=\"1.0\"?><package><metadata><id>" + id + "</id><version>" + version
            + "</version><authors>Tests</authors><description>Asset selection fixture</description>" + dependencies + "</metadata></package>");
        foreach (string asset in assets)
        {
            Write(archive, asset, asset);
        }
    }

    /// <summary>製品の復元処理を、専用キャッシュとローカルフィードで実行します。</summary>
    /// <returns>実行対象へ公開される資産一覧です。</returns>
    internal Task<ScriptPackageEnvironment> RestoreAsync(IReadOnlyList<ScriptPackageReference> packages, CancellationToken cancellationToken = default)
    {
        return NugetScriptPackages.InstallAsync(packages, DirectoryPath, Path.Combine(DirectoryPath, "cache"), Path.Combine(DirectoryPath, "feed"), cancellationToken);
    }

    /// <summary>復元結果をNuGetが永続化した形式のまま読み取ります。</summary>
    /// <returns>呼び出し側が所有するJSON文書です。</returns>
    internal JsonDocument Assets()
    {
        return JsonDocument.Parse(File.ReadAllBytes(Path.Combine(DirectoryPath, "project.assets.json")));
    }

    /// <summary>この試験が所有する復元先だけを削除します。</summary>
    public void Dispose()
    {
        Directory.Delete(DirectoryPath, true);
    }

    /// <summary>ローカルNuGet配布物の一項目を保存します。</summary>
    private static void Write(ZipArchive archive, string path, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(path).Open(), new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }
}
