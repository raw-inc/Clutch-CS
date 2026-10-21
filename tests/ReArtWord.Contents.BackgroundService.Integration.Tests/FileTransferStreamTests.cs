using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Files;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>同じ実Coreと内容で従来UDPとTCPを比較し、完成後も内容一致を検証します。</summary>
public sealed class FileTransferStreamTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>独立Core間で8MiBを繰り返しコピーし、全体検証を含む実測値を残します。</summary>
    /// <returns>新旧経路と共有キューの実通信検証です。</returns>
    [Fact]
    public async Task TransfersLargeFilesOverBothTransportsWithoutChangingIntegrity()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer source = new(cluster, "source", port, true, "source-host");
        CorePeer destination = new(cluster, "destination", port, false, "destination-host");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = source.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = destination.ConfigureAwait(true);
        await source.StartAsync().ConfigureAwait(true);
        await destination.StartAsync().ConfigureAwait(true);
        UdpObserver observer = new(new ObserverOptions("stream-test", new ClientNetworkBinding(source.InterfaceId, "127.0.0.1", "127.255.255.255", port)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = ((IAsyncDisposable)observer).ConfigureAwait(true);
        await observer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await CorePeer.WaitAsync(_ => Task.FromResult(observer.GetClusters().SelectMany(item => item.Peers).Count() == 2), timeout.Token).ConfigureAwait(true);
        JsonElement sourceHealth = await source.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        JsonElement destinationHealth = await destination.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        FileTransferPeer from = new(cluster, "source", sourceHealth.GetProperty("instanceId").GetGuid(), port);
        FileTransferPeer to = new(cluster, "destination", destinationHealth.GetProperty("instanceId").GetGuid(), port);
        PeerManagementClient remote = new((_, _) => observer);
        string path = Path.Combine(source.DirectoryPath, "日本語-large.bin");
        byte[] bytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 17);
        await File.WriteAllBytesAsync(path, bytes, timeout.Token).ConfigureAwait(true);
        using FileTransferClient reader = await remote.OpenFileTransferAsync(from, timeout.Token).ConfigureAwait(true);
        using FileTransferClient writer = await remote.OpenFileTransferAsync(to, timeout.Token).ConfigureAwait(true);
        Assert.Equal(FileTransferLimits.StreamBytes, reader.ChunkBytes);
        Assert.Equal(FileTransferLimits.StreamBytes, writer.ChunkBytes);
        FileEntryInfo file = await reader.StatAsync(path, timeout.Token).ConfigureAwait(true);
        foreach (bool stream in new[] { false, true, true })
        {
            string destinationPath = Path.Combine(destination.DirectoryPath, Guid.NewGuid().ToString("N") + ".bin");
            FileStageToken stage = await remote.SendAsync<FileStageToken>(to, "transfer.open", new FileStageRequest(Guid.NewGuid().ToString("N"),
                "source", path, destinationPath, FileTransferManifest.ComputeSha256([new(string.Empty, false, file.Length, file.Sha256)]), false), false, timeout.Token).ConfigureAwait(true);
            Stopwatch timer = Stopwatch.StartNew();
            int count = FileTransferLimits.DatagramBytes;
            if (stream)
            {
                count = Math.Min(reader.ChunkBytes, writer.ChunkBytes);
            }
            for (long offset = 0; offset < file.Length; offset += count)
            {
                FileChunkRequest request = new(path, offset, count, file.Length, file.Sha256);
                FileChunk chunk;
                if (stream)
                {
                    chunk = await reader.ReadAsync(request, timeout.Token).ConfigureAwait(true);
                }
                else
                {
                    chunk = await remote.SendAsync<FileChunk>(from, "transfer.read", request, true, timeout.Token).ConfigureAwait(true);
                }
                FileChunkWriteRequest write = new(stage, string.Empty, offset, chunk.Data, chunk.Sha256, file.Length, file.Sha256);
                if (stream)
                {
                    await writer.WriteAsync(write, timeout.Token).ConfigureAwait(true);
                }
                else
                {
                    _ = await remote.SendAsync<bool>(to, "transfer.write", write, false, timeout.Token).ConfigureAwait(true);
                }
            }
            FileStageCommitResult result = await remote.SendAsync<FileStageCommitResult>(to, "transfer.commit", stage, false, timeout.Token).ConfigureAwait(true);
            timer.Stop();
            output.WriteLine("TCP={0}; {1} bytes; {2:F3} s; {3:F2} MiB/s including final verification", stream, result.Bytes, timer.Elapsed.TotalSeconds,
                result.Bytes / 1048576d / timer.Elapsed.TotalSeconds);
            Assert.Equal(file.Length, result.Bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destinationPath, timeout.Token).ConfigureAwait(true));
            _ = await remote.SendAsync<bool>(to, "transfer.discard", stage, false, timeout.Token).ConfigureAwait(true);
        }
        string queuedPath = Path.Combine(destination.DirectoryPath, Path.GetFileName(path));
        FileTransferRequest queued = new(Guid.NewGuid(), from, to, path, destination.DirectoryPath);
        _ = await destination.PostAsync<FileTransferJob>("transfer.queue.enqueue", queued, timeout.Token).ConfigureAwait(true);
        FileTransferJob? completed = null;
        await CorePeer.WaitAsync(async token =>
        {
            completed = (await destination.GetAsync<FileTransferJob[]>("transfer.queue.list", token).ConfigureAwait(true)).Single(job => job.Request.Id == queued.Id);
            return completed.State is not ("Queued" or "Copying");
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal("Succeeded", completed!.State);
        Assert.Equal(file.Length, completed.BytesCopied);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(queuedPath, timeout.Token).ConfigureAwait(true));
    }
}
