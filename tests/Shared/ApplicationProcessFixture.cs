using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Tests.Shared;

/// <summary>実アプリケーションの専用コピーと、試験で開始した実体だけを所有します。</summary>
internal sealed class ApplicationProcessFixture : IDisposable
{
    private readonly TestProcessHandles owned = new();

    /// <summary>引数なしでも動く実行物と、試験専用の終了条件を配置します。</summary>
    internal ApplicationProcessFixture(int exitCode = 0, int delayMilliseconds = 0, bool withArguments = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "clutch-application-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string source = Path.Combine(repository, "tests", "ReArtWord.Contents.BackgroundService.Unit.Tests", "CommandLine", "FixtureHost", "bin", configuration, "net10.0");
        string suffix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            suffix = ".exe";
        }

        // 並列の別試験を後継候補へ入れないよう、実行ファイル名も分離します。
        string original = "ContentsCommandLineFixture" + suffix;
        string executable = "app-" + Guid.NewGuid().ToString("N")[..10] + suffix;
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(Root, string.Equals(name, original, StringComparison.Ordinal) ? executable
                                                                                                       : name));
        }
        File.WriteAllText(Path.Combine(Root, "application-fixture.json"), JsonSerializer.Serialize(new { exitCode, delayMs = delayMilliseconds }, ContractJson.Options));
        IReadOnlyList<string> arguments = [];
        if (withArguments)
        {
            arguments = ["--scene", "日本語 space", "quoted\"value", string.Empty];
        }
        Definition = new("fixture-app", EProcessMode.Managed, Path.Combine(Root, executable), arguments, Root,
            LogicalName: "Test application", AutomaticMonitoring: true);
    }

    /// <summary>試験以外のアプリケーションへ影響しないコピー先です。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>そのまま管理APIへ登録できる定義です。</summary>
    internal ProcessDefinition Definition
    {
        get;
    }

    /// <summary>起動回ごとのOS識別子と実際の引数を読みます。</summary>
    /// <returns>原子的に完成した起動記録だけです。</returns>
    internal string[][] Starts()
    {
        string[][] entries = Directory.GetFiles(Root, "*.started").Select(PublishedFixtureFile.ReadAllLines).ToArray();
        foreach (string[] entry in entries)
        {
            owned.Capture(new(int.Parse(entry[0], CultureInfo.InvariantCulture), DateTimeOffset.Parse(entry[1], CultureInfo.InvariantCulture)));
        }
        return entries;
    }

    /// <summary>長時間動く試験アプリケーションを自明終了させます。</summary>
    internal void Release()
    {
        _ = Starts();
        File.WriteAllText(Path.Combine(Root, "release"), string.Empty);
    }

    /// <summary>このコピーから起動した実体をPIDと開始時刻で再照合して解放します。</summary>
    public void Dispose()
    {
        try
        {
            _ = Starts();
        }
        finally
        {
            owned.Dispose();
        }
        TestProcessDirectory.Delete(Root);
    }
}
