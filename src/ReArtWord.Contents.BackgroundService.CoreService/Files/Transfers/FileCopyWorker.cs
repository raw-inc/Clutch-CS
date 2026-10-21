using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>有限マニフェストと既存の検証済みstage転送を使う一件のコピーです。</summary>
internal sealed class FileCopyWorker(PeerManagementClient remote)
{
    /// <summary>全内容を検証してから宛先へ確定し、受信不能時に再実行しません。</summary>
    /// <returns>最終状態です。</returns>
    internal async Task<FileTransferJob> CopyAsync(FileTransferJob job, Action<FileTransferJob> report, CancellationToken cancellationToken = default)
    {
        FileTransferRequest request = job.Request;
        FileStageToken? stage = null;
        bool committing = false;
        string phase = "source enumeration";
        try
        {
            using FileTransferClient source = await remote.OpenFileTransferAsync(request.Source, cancellationToken).ConfigureAwait(false);
            List<(FileEntryInfo Info, string Relative)> entries = await EnumerateAsync(request, source, cancellationToken).ConfigureAwait(false);
            string sourceName = request.SourcePath.TrimEnd('/', '\\').Split('/', '\\')[^1];
            string[] invalidNames = [string.Empty, ".", ".."];
            string separator = "/";
            if (invalidNames.Contains(sourceName, StringComparer.Ordinal) || sourceName.Contains(':', StringComparison.Ordinal))
            {
                throw new IOException("Choose a named file or folder.");
            }
            string destination = request.DestinationDirectory.TrimEnd('/', '\\') + "/" + sourceName;
            if (request.Source == request.Destination && (string.Equals(request.SourcePath.Replace('\\', '/').TrimEnd('/'), destination, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(request.SourcePath.Replace('\\', '/').TrimEnd('/') + separator, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("A folder cannot be copied into itself.");
            }
            FileManifestEntry[] manifest = entries.Select(item => new FileManifestEntry(item.Relative, item.Info.IsDirectory, item.Info.Length, item.Info.Sha256)).ToArray();
            job = job with
            {
                State = "Copying",
                TotalBytes = manifest.Sum(entry => entry.Length),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            report(job);
            phase = "destination preparation";
            using FileTransferClient destinationClient = await remote.OpenFileTransferAsync(request.Destination, cancellationToken).ConfigureAwait(false);
            int chunkBytes = Math.Min(source.ChunkBytes, destinationClient.ChunkBytes);
            stage = await remote.SendAsync<FileStageToken>(request.Destination, "transfer.open", new FileStageRequest(request.Id.ToString("N"),
                request.Source.ClusterId + "/" + request.Source.PeerId, request.SourcePath, destination, FileTransferManifest.ComputeSha256(manifest), entries[0].Info.IsDirectory), false, cancellationToken).ConfigureAwait(false);
            foreach ((FileEntryInfo info, string relative) in entries)
            {
                phase = "data transfer";
                if (info.IsDirectory)
                {
                    _ = await remote.SendAsync<bool>(request.Destination, "transfer.directory", new FileStageDirectoryRequest(stage, relative), false, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                long offset = 0;
                do
                {
                    FileChunk chunk = await source.ReadAsync(new FileChunkRequest(info.Path, offset, chunkBytes, info.Length, info.Sha256), cancellationToken).ConfigureAwait(false);
                    if (chunk.Offset != offset || (chunk.Data.Length == 0 && offset < info.Length))
                    {
                        throw new IOException("Invalid chunk position.");
                    }
                    await destinationClient.WriteAsync(new FileChunkWriteRequest(stage, relative, offset, chunk.Data, chunk.Sha256, info.Length, info.Sha256), cancellationToken).ConfigureAwait(false);
                    offset += chunk.Data.Length;
                    job = job with
                    {
                        BytesCopied = job.BytesCopied + chunk.Data.Length,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    report(job);
                }
                while (offset < info.Length);
            }
            committing = true;
            _ = await remote.SendAsync<FileStageCommitResult>(request.Destination, "transfer.commit", stage, false, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await remote.SendAsync<bool>(request.Destination, "transfer.discard", stage, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (cleanup is IOException or OperationCanceledException)
            {
                return job with
                {
                    State = "Succeeded",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Problem = "Copy completed; temporary transfer metadata cleanup is unconfirmed."
                };
            }
            return job with
            {
                State = "Succeeded",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException)
        {
            if (stage is not null && !committing)
            {
                try
                {
                    _ = await remote.SendAsync<bool>(request.Destination, "transfer.discard", stage, false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is IOException or OperationCanceledException)
                {
                    /* 保持されたstageを成功と扱いません。 */
                }
            }
            string state = "Failed";
            string problem = "Copy failed during " + phase + ". Check service-account access, links, peer connectivity, source changes and destination conflicts.";
            if (committing)
            {
                state = "Unknown";
                problem = "Final copy result is unconfirmed. Inspect the destination before starting another copy.";
            }
            return job with
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                Problem = problem
            };
        }
    }

    /// <summary>一件の走査量を10000項目までに制限し、ディレクトリ構造を維持します。</summary>
    /// <returns>固定した内容版と相対パスです。</returns>
    private static async Task<List<(FileEntryInfo Info, string Relative)>> EnumerateAsync(FileTransferRequest request, FileTransferClient source, CancellationToken cancellationToken = default)
    {
        FileEntryInfo root = await source.StatAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
        List<(FileEntryInfo Info, string Relative)> result = [(root, string.Empty)];
        for (int index = 0; index < result.Count; index++)
        {
            (FileEntryInfo info, string relative) = result[index];
            if (!info.IsDirectory)
            {
                continue;
            }
            string? cursor = null;
            do
            {
                FileListPage page = await source.ListAsync(new FileTransferListRequest(info.Path, cursor), cancellationToken).ConfigureAwait(false);
                foreach (FileEntryInfo child in page.Entries)
                {
                    if (10000 <= result.Count)
                    {
                        throw new IOException("Copy selection exceeds 10000 entries.");
                    }
                    string name = child.Path.Replace('\\', '/').Split('/')[^1];
                    string childRelative = name;
                    if (relative.Length != 0)
                    {
                        childRelative = relative + "/" + name;
                    }
                    result.Add((child, childRelative));
                }
                if (page.Continuation is not null && string.Equals(cursor, page.Continuation, StringComparison.Ordinal))
                {
                    throw new IOException("File enumeration did not advance.");
                }
                cursor = page.Continuation;
            }
            while (cursor is not null);
        }
        return result;
    }
}
