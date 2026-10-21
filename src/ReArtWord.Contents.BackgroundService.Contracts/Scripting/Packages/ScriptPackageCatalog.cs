namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>選択したPeerの現在構成と、有限件の操作結果です。</summary>
public sealed record ScriptPackageCatalog(ScriptPackageEnvironmentInfo? Nuget, ScriptPackageEnvironmentInfo? Pip,
    IReadOnlyList<ScriptPackageOperation> Operations, bool Busy,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ScriptPackageEnvironmentInfo? Npm = null);
