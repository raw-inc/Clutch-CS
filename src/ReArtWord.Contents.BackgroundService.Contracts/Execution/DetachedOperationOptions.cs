namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>明示離脱中に許可する独立ローカル操作を用途別に指定します。</summary>
/// <param name="Edit">ローカルの未共有編集を許可するか。</param>
/// <param name="Compute">計算・観測・読取りを許可するか。</param>
/// <param name="FileChange">ファイル変更を許可するか。</param>
/// <param name="ProcessChange">プロセス操作・自動復旧を許可するか。</param>
/// <param name="ExternalCommunication">外部通信の送受信を許可するか。</param>
public sealed record DetachedOperationOptions(
    bool Edit = true,
    bool Compute = true,
    bool FileChange = true,
    bool ProcessChange = true,
    bool ExternalCommunication = true);
