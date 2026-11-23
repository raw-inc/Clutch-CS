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
        JsonElement sourceHealth = await source.StartAsync().ConfigureAwait(true);
        JsonElement destinationHealth = await destination.StartAsync().ConfigureAwait(true);
        await WaitForPeersAsync(destination).ConfigureAwait(true);
        string from = Path.Combine(source.DirectoryPath, "outside-roots", "folder");
        string to = Path.Combine(destination.DirectoryPath, "outside-roots");
        Directory.CreateDirectory(Path.Combine(from, "empty"));
        Directory.CreateDirectory(to);
        byte[] bytes = Enumerable.Range(0, 80000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(from, "file.bin"), bytes).ConfigureAwait(true);
        FileTransferRequest request = new(Guid.NewGuid(), new(cluster, "source", sourceHealth.GetProperty("instanceId").GetGuid(), port),
            new(cluster, "destination", destinationHealth.GetProperty("instanceId").GetGuid(), port), from, to);
        FileTransferJob result = await CopyAsync(destination, request).ConfigureAwait(true);
        Assert.Equal("Succeeded", result.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(to, "folder", "file.bin")).ConfigureAwait(true));
        Assert.True(Directory.Exists(Path.Combine(to, "folder", "empty")));
        FileTransferRequest repeat = request with { Id = Guid.NewGuid() };
        result = await CopyAsync(destination, repeat).ConfigureAwait(true);
        Assert.NotEqual("Succeeded", result.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(to, "folder", "file.bin")).ConfigureAwait(true));
    }

    /// <summary>同名ファイルは既定で保持され、明示的に上書きを選んだ場合だけ置き換えられることを確認します。</summary>
    /// <returns>実Core間のコピー結果を検証するタスクです。</returns>
    [Fact]
    public async Task ReplacesExistingFileOnlyWhenOverwriteIsRequested()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer source = new(cluster, "source", port, true, "source-host");
        CorePeer destination = new(cluster, "destination", port, false, "destination-host");
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable sourceLifetime = ((IAsyncDisposable)source).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable destinationLifetime = ((IAsyncDisposable)destination).ConfigureAwait(true);
        JsonElement sourceHealth = await source.StartAsync().ConfigureAwait(true);
        JsonElement destinationHealth = await destination.StartAsync().ConfigureAwait(true);
        await WaitForPeersAsync(destination).ConfigureAwait(true);
        string from = Path.Combine(source.DirectoryPath, "new.txt");
        string to = Path.Combine(destination.DirectoryPath, "new.txt");
        await File.WriteAllTextAsync(from, "replacement").ConfigureAwait(true);
        await File.WriteAllTextAsync(to, "original").ConfigureAwait(true);
        FileTransferRequest request = new(Guid.NewGuid(), new(cluster, "source", sourceHealth.GetProperty("instanceId").GetGuid(), port),
            new(cluster, "destination", destinationHealth.GetProperty("instanceId").GetGuid(), port), from, destination.DirectoryPath);

        FileTransferJob denied = await CopyAsync(destination, request).ConfigureAwait(true);
        Assert.NotEqual("Succeeded", denied.State);
        Assert.Equal("original", await File.ReadAllTextAsync(to).ConfigureAwait(true));

        FileTransferRequest replace = request with { Id = Guid.NewGuid(), Overwrite = true };
        FileTransferJob copied = await CopyAsync(destination, replace).ConfigureAwait(true);
        Assert.Equal("Succeeded", copied.State);
        Assert.True(copied.Request.Overwrite);
        Assert.Equal("replacement", await File.ReadAllTextAsync(to).ConfigureAwait(true));

        string sourceFolder = Path.Combine(source.DirectoryPath, "folder");
        string destinationFolder = Path.Combine(destination.DirectoryPath, "folder");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(destinationFolder);
        string preserved = Path.Combine(destinationFolder, "keep.txt");
        await File.WriteAllTextAsync(preserved, "keep").ConfigureAwait(true);
        FileTransferJob folder = await CopyAsync(destination, replace with { Id = Guid.NewGuid(), SourcePath = sourceFolder }).ConfigureAwait(true);
        Assert.NotEqual("Succeeded", folder.State);
        Assert.Equal("keep", await File.ReadAllTextAsync(preserved).ConfigureAwait(true));
    }

    /// <summary>転送元と転送先が管理要求を受け付けられるまで待ちます。</summary>
    /// <returns>両方のCoreがReadyになった時点で完了します。</returns>
    private static Task WaitForPeersAsync(CorePeer destination)
        => CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await destination.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        });

    /// <summary>転送をキューに追加し、転送先が結果を記録するまで待ちます。</summary>
    /// <returns>キューに記録された転送結果を返すタスクです。</returns>
    private static async Task<FileTransferJob> CopyAsync(CorePeer destination, FileTransferRequest request)
    {
        _ = await destination.PostAsync<FileTransferJob>("transfer.queue.enqueue", request).ConfigureAwait(true);
        FileTransferJob? result = null;
        await CorePeer.WaitAsync(async token =>
        {
            result = (await destination.GetAsync<FileTransferJob[]>("transfer.queue.list", token).ConfigureAwait(true)).Single(job => job.Request.Id == request.Id);
            return result.State is not ("Queued" or "Copying");
        }).ConfigureAwait(true);
        return result!;
    }
}
