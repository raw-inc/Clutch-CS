using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core間の管理UI用キューがOSアクセス権の範囲で転送することを確認します。</summary>
public sealed class SharedFileTransferTests
{
    /// <summary>旧roots外のフォルダーと空ディレクトリを転送し、同名宛先を上書きしません。</summary>
    /// <returns>実通信と実ファイルで確認した共有キューの結果です。</returns>
    [Fact]
    public async Task CopiesOutsideLegacyRootsAndPreservesExistingDestination()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer source = new(cluster, "source", port, true, "source-host");
        CorePeer destination = new(cluster, "destination", port, false, "destination-host");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = ((IAsyncDisposable)destination).ConfigureAwait(true);
        await source.StartAsync().ConfigureAwait(true);
        await destination.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await destination.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        }).ConfigureAwait(true);
        string from = Path.Combine(source.DirectoryPath, "outside-roots", "folder");
        string to = Path.Combine(destination.DirectoryPath, "outside-roots");
        Directory.CreateDirectory(Path.Combine(from, "empty"));
        Directory.CreateDirectory(to);
        byte[] bytes = Enumerable.Range(0, 80000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(from, "file.bin"), bytes).ConfigureAwait(true);
        JsonElement sourceHealth = await source.GetAsync<JsonElement>("health").ConfigureAwait(true), destinationHealth = await destination.GetAsync<JsonElement>("health").ConfigureAwait(true);
        FileTransferRequest request = new(Guid.NewGuid(), new(cluster, "source", sourceHealth.GetProperty("instanceId").GetGuid(), port),
            new(cluster, "destination", destinationHealth.GetProperty("instanceId").GetGuid(), port), from, to);
        _ = await destination.PostAsync<FileTransferJob>("transfer.queue.enqueue", request).ConfigureAwait(true);
        FileTransferJob? result = null;
        await CorePeer.WaitAsync(async token =>
        {
            result = (await destination.GetAsync<FileTransferJob[]>("transfer.queue.list", token).ConfigureAwait(true)).Single(job => job.Request.Id == request.Id);
            return result.State is not ("Queued" or "Copying");
        }).ConfigureAwait(true);
        Assert.Equal("Succeeded", result!.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(to, "folder", "file.bin")).ConfigureAwait(true));
        Assert.True(Directory.Exists(Path.Combine(to, "folder", "empty")));
        FileTransferRequest repeat = request with { Id = Guid.NewGuid() };
        _ = await destination.PostAsync<FileTransferJob>("transfer.queue.enqueue", repeat).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            result = (await destination.GetAsync<FileTransferJob[]>("transfer.queue.list", token).ConfigureAwait(true)).Single(job => job.Request.Id == repeat.Id);
            return result.State is not ("Queued" or "Copying");
        }).ConfigureAwait(true);
        Assert.NotEqual("Succeeded", result!.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(to, "folder", "file.bin")).ConfigureAwait(true));
    }
}
