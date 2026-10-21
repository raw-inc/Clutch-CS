using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Installation;

/// <summary>導入時に保存した配置の所有証明です。削除範囲を外部入力から組み立てません。</summary>
public sealed record InstallationRecord(Guid InstallationId, InstallationSettings Settings, string ServiceName, string CoreEntryPoint,
    string UpdaterEntryPoint, string ClientEntryPoint, DateTimeOffset InstalledAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebClientInstallation? WebClient = null);
