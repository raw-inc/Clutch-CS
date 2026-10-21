using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>各Hostで正規化したパスの包含関係を比較します。</summary>
public static class FileOverlap
{
    /// <summary>read/readを許し、同Hostの変更重複だけを検出します。</summary>
    /// <returns>同時実行できない範囲か。</returns>
    public static bool Conflicts(FileFootprint first, FileFootprint second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return string.Equals(first.HostId, second.HostId, StringComparison.Ordinal) && (first.Write || second.Write)
            && (string.Equals(first.Path, second.Path, StringComparison.Ordinal) || first.Path.StartsWith(second.Path.TrimEnd('/') + "/", StringComparison.Ordinal) || second.Path.StartsWith(first.Path.TrimEnd('/') + "/", StringComparison.Ordinal));
    }
}
