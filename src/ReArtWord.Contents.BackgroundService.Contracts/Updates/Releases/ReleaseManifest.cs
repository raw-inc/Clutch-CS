namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;

/// <summary>一つのクラスタリリースとして検証する全ファイルの契約です。</summary>
/// <param name="ReleaseId">明示リリース識別子です。</param>
/// <param name="Platform">win-x64等の対象runtime識別子です。</param>
/// <param name="EntryPoint">リリース内の起動entry相対パスです。</param>
/// <param name="Files">適用する全ファイルのサイズとSHA256です。</param>
/// <param name="CompatibleReleaseIds">通常混在稼働が確認済みのリリース識別子です。</param>
/// <param name="ExpectedBuildVersion">稼働確認へ要求するbuildVersion。未指定でも実ファイルハッシュを必ず検証します。</param>
public sealed record ReleaseManifest(string ReleaseId, string Platform, string EntryPoint, IReadOnlyList<ReleaseFile> Files, IReadOnlyList<string> CompatibleReleaseIds, string? ExpectedBuildVersion = null);
