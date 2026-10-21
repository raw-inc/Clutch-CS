namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>ピア側の共有キューを集約した全ClientHost向けの状態です。</summary>
/// <param name="Jobs">最後に実確認したジョブです。</param>
/// <param name="UnconfirmedPeers">今回確認できなかったピア数です。</param>
/// <param name="TotalPeers">今回の取得対象ピア数です。</param>
/// <param name="TotalJobs">全キューの件数です。</param>
/// <param name="Offset">このページの開始位置です。</param>
public sealed record SharedTransferQueueState(IReadOnlyList<FileTransferJob> Jobs, int UnconfirmedPeers = 0, int TotalPeers = 0, int TotalJobs = 0, int Offset = 0);
