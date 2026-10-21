namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>明示設定した共有キュー所有先の成立状態です。</summary>
/// <param name="Enabled">このCoreが所有先として設定されているかです。</param>
/// <param name="Ready">保存済みキューと監視の準備ができたかです。</param>
public sealed record FileTransferQueueInfo(bool Enabled, bool Ready);
