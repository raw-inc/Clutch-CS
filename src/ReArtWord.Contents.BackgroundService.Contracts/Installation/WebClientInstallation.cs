namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>クラスターから独立して保存するWebクライアントの導入情報です。</summary>
public sealed record WebClientInstallation(Guid InstallationId, string Root, string EntryPoint, int Port, bool PerUser,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool AutomaticStartup = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RunAsService = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ProgramDirectory = null);
