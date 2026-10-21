using ReArtWord.Contents.BackgroundService.Contracts.Installation;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>CoreとWebクライアントの自動起動に必要な情報です。</summary>
internal sealed record StartupRegistration(string Name, string Executable, string[] Arguments, bool PerUser, string DisplayName)
{
    /// <summary>既存のCoreの起動引数を維持します。</summary>
    /// <returns>Coreの登録内容です。</returns>
    internal static StartupRegistration ForCore(InstallationRecord record) => new(record.ServiceName,
        Path.Combine(record.Settings.Root, "core", record.CoreEntryPoint),
        ["--config", Path.Combine(record.Settings.Root, "config", "contents.toml")], record.Settings.PerUser,
        "Clutch-CS · " + record.Settings.ClusterId + " / " + record.Settings.PeerId);

    /// <summary>手動起動と自動起動で同じ保存先・ポートを使います。</summary>
    /// <returns>Webクライアントの登録内容です。</returns>
    internal static StartupRegistration ForClient(WebClientInstallation client) => new(WebClientStartup.ServiceName(client),
        Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint),
        ["--state", WebClientStartup.StateDirectory(client), "--listen", ClientLaunchFiles.GetUrl(client.Port).OriginalString],
        !client.RunAsService, "Clutch-CS Web Client");

    /// <summary>従来のCore登録と同じWindowsの引用形式に揃えます。</summary>
    internal string Command => Quote(Executable) + " " + string.Join(" ", Arguments.Select(FormatArgument));

    /// <summary>オプション名は従来どおり引用せず、値を引用します。</summary>
    /// <returns>コマンドに含める引数です。</returns>
    private static string FormatArgument(string argument)
    {
        string optionPrefix = "--";
        if (argument.StartsWith(optionPrefix, StringComparison.Ordinal))
        {
            return argument;
        }
        return Quote(argument);
    }

    /// <summary>Windowsの単一引数として引用します。</summary>
    /// <returns>引用済み引数です。</returns>
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
