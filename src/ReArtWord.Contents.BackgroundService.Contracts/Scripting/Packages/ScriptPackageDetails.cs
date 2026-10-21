namespace ReArtWord.Contents.BackgroundService.Contracts.Scripting.Packages;

/// <summary>インストール前に確認する配布名・説明・公開版です。</summary>
public sealed record ScriptPackageDetails(string Id, string Description, string LatestVersion, IReadOnlyList<string> Versions,
    Uri? RegistryUrl, bool VersionsTruncated = false, string? Problem = null);
