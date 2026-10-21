using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>共通キューの受付と実行開始・終了境界を原子的に保存します。</summary>
internal sealed class FileTransferJournal(string directory)
{
    /// <summary>再起動後の実行中ジョブを結果不明として保持し、暗黙に再実行しません。</summary>
    /// <returns>有限の保存済みキューです。</returns>
    internal async Task<FileTransferJob[]> LoadAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, "queue.json");
        if (!File.Exists(path))
        {
            return [];
        }
        if (8 * 1024 * 1024 < new FileInfo(path).Length)
        {
            throw new InvalidDataException("Transfer queue journal exceeds its limit.");
        }
        FileTransferJob[] jobs = JsonSerializer.Deserialize<FileTransferJob[]>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("Transfer queue journal is invalid.");
        if (133 < jobs.Length)
        {
            throw new InvalidDataException("Transfer queue journal has too many entries.");
        }
        return jobs.Select(job =>
        {
            string copying = "Copying";
            if (string.Equals(job.State, copying, StringComparison.Ordinal))
            {
                return job with
                {
                    State = "Unknown",
                    Problem = "Queue Core restarted during this copy. Inspect the destination; the copy was not replayed.",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            return job;
        }).ToArray();
    }

    /// <summary>書出し完了前に受付を確定しません。</summary>
    /// <returns>キューの保存完了です。</returns>
    internal async Task SaveAsync(FileTransferJob[] jobs, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "queue.json");
        string pending = path + ".pending";
        using (FileStream stream = new(pending, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, jobs, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(pending, path, true);
    }
}
