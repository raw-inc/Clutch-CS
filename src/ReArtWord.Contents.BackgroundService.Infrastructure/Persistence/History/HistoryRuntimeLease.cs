namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>稼働Coreが保存を開く前から、停止中専用の展開・全体縮小との重なりを拒否します。</summary>
public static class HistoryRuntimeLease
{
    /// <summary>Coreの全寿命で保持し、履歴保守の専有取得と両立させません。</summary>
    /// <returns>Core終了まで呼出元が保持する利用記録です。</returns>
    public static FileStream Acquire(string directory) => Open(directory, FileShare.ReadWrite);

    /// <summary>明示した停止中保守だけが、保存先を開く前に専有します。</summary>
    /// <returns>保守の全寿命で保持する専有記録です。</returns>
    public static FileStream AcquireOffline(string directory) => Open(directory, FileShare.None);

    /// <summary>解決済みの保存先だけへ同じ固定名を使用します。</summary>
    /// <returns>所有元が破棄するhandleです。</returns>
    private static FileStream Open(string directory, FileShare sharing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("保存先は絶対パスで指定してください。", nameof(directory));
        }
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "history-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, sharing);
    }
}
