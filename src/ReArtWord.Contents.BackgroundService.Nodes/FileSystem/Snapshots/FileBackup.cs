namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Snapshots;

/// <summary>変更する一つのパスの実行前状態です。</summary>
/// <param name="Path">復元先。</param>
/// <param name="StoredPath">退避先。元が不存在の場合はnull。</param>
/// <param name="ManifestSha256">復元前に照合する退避全体のdigest。</param>
internal sealed record FileBackup(string Path, string? StoredPath, string? ManifestSha256);
