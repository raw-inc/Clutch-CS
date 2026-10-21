namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>段階内に作る相対ディレクトリです。</summary>
/// <param name="Stage">Stageの指定値。</param>
/// <param name="RelativePath">RelativePathの指定値。</param>
public sealed record FileStageDirectoryRequest(FileStageToken Stage, string RelativePath);
