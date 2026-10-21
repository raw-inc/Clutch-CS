namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>新しい解析に使用する構成と、そのPeerで確認した依存関係です。</summary>
public sealed record ScriptPackageEnvironmentInfo(EScriptPackageKind Kind, string Revision,
    IReadOnlyList<ScriptPackageReference> Requested, IReadOnlyList<ScriptPackageReference> Installed,
    DateTimeOffset CreatedAt, string? PythonPath = null, string? PythonVersion = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NodePath = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NodeVersion = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NpmPath = null);
