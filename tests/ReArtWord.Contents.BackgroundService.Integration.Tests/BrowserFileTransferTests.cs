using System.Net.Http.Json;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ブラウザー用HTTP中継を、別プロセスのCoreと実ファイルで検証します。</summary>
public sealed class BrowserFileTransferTests
{
    /// <summary>空ファイルを含む分割アップロードとダウンロード、段階回収、実体固定を確認します。</summary>
    /// <returns>実HTTPとPeer通信の検証です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(80000, false)]
    [InlineData(2097169, false)]
    [InlineData(0, true)]
    [InlineData(80000, true)]
    [InlineData(2097169, true)]
    public async Task TransfersVerifiedFilesAndDiscardsOnlyStagingAsync(int length, bool directory)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "target", port, true);
        ClientHostProcess client = new(Path.Combine(core.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = core.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        using HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true);
        binding.EnsureSuccessStatusCode();
        Guid instance = Guid.Empty;
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(true);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(true);
            instance = clusters?.SelectMany(item => item.Peers).FirstOrDefault()?.InstanceId ?? Guid.Empty;
            return instance != Guid.Empty;
        }).ConfigureAwait(true);
        string destination = Path.Combine(core.DirectoryPath, "browser file 日本語.bin");
        byte[] bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string relativePath = string.Empty;
        List<FileManifestEntry> entries = [];
        if (directory)
        {
            relativePath = "日本語/file.bin";
            entries.Add(new(string.Empty, true, 0, string.Empty));
            entries.Add(new("日本語", true, 0, string.Empty));
            entries.Add(new("empty", true, 0, string.Empty));
        }
        entries.Add(new(relativePath, false, length, hash));
        string manifest = FileTransferManifest.ComputeSha256(entries);
        using HttpResponseMessage computed = await client.SendAsync(HttpMethod.Post, "client/browser-files/manifest", entries).ConfigureAwait(true);
        computed.EnsureSuccessStatusCode();
        using System.Text.Json.JsonDocument metadata = await System.Text.Json.JsonDocument.ParseAsync(await computed.Content.ReadAsStreamAsync().ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(manifest, metadata.RootElement.GetProperty("sha256").GetString());
        FileStageRequest open = new(Guid.NewGuid().ToString("D"), "browser", "file.bin", destination, manifest, directory);
        using HttpResponseMessage opened = await client.SendAsync(HttpMethod.Post, "api/browser-files/open", open, cluster, "target").ConfigureAwait(true);
        opened.EnsureSuccessStatusCode();
        FileStageToken stage = (await opened.Content.ReadFromJsonAsync<FileStageToken>(ContractJson.Options).ConfigureAwait(true))!;
        foreach (FileManifestEntry entry in entries.Where(entry => entry.IsDirectory))
        {
            using HttpResponseMessage created = await client.SendAsync(HttpMethod.Post, "api/browser-files/directory", new FileStageDirectoryRequest(stage, entry.RelativePath), cluster, "target").ConfigureAwait(true);
            created.EnsureSuccessStatusCode();
        }
        using HttpClient uploadClient = new() { BaseAddress = client.Address };
        for (int offset = 0; offset < length || offset == 0; offset += 2 * 1024 * 1024)
        {
            int count = Math.Min(2 * 1024 * 1024, length - offset);
            string partHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count)));
            using HttpRequestMessage request = new(HttpMethod.Post, $"client/browser-files/write?stage={stage.Id:D}&relativePath={Uri.EscapeDataString(relativePath)}&offset={offset}&count={count}&length={length}&sha256={hash}&partSha256={partHash}");
            request.Headers.Add("X-Contents-Cluster", cluster);
            request.Headers.Add("X-Contents-Peer", "target");
            request.Headers.Add("X-Contents-Instance", instance.ToString("D"));
            request.Content = new ByteArrayContent(bytes, offset, count);
            if (directory && offset == 0)
            {
                using HttpRequestMessage escaped = new(HttpMethod.Post, request.RequestUri!.OriginalString.Replace(Uri.EscapeDataString(relativePath), "..%2Foutside.bin", StringComparison.Ordinal));
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                {
                    escaped.Headers.Add(header.Key, header.Value);
                }
                escaped.Content = new ByteArrayContent(bytes, offset, count);
                using HttpResponseMessage rejectedPath = await uploadClient.SendAsync(escaped).ConfigureAwait(true);
                Assert.False(rejectedPath.IsSuccessStatusCode);
                Assert.False(File.Exists(Path.Combine(core.DirectoryPath, "outside.bin")));
            }
            if (offset == 0)
            {
                using HttpRequestMessage invalid = new(HttpMethod.Post, request.RequestUri!.ToString().Replace(partHash, "invalid", StringComparison.Ordinal));
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                {
                    invalid.Headers.Add(header.Key, header.Value);
                }
                invalid.Content = new ByteArrayContent(bytes, offset, count);
                using HttpResponseMessage rejected = await uploadClient.SendAsync(invalid).ConfigureAwait(true);
                Assert.False(rejected.IsSuccessStatusCode);
                Assert.False(File.Exists(destination));
            }
            using HttpResponseMessage written = await uploadClient.SendAsync(request).ConfigureAwait(true);
            written.EnsureSuccessStatusCode();
            if (length == 0)
            {
                break;
            }
        }
        using HttpResponseMessage committed = await client.SendAsync(HttpMethod.Post, "api/browser-files/commit", stage, cluster, "target", Guid.NewGuid()).ConfigureAwait(true);
        committed.EnsureSuccessStatusCode();
        if (directory)
        {
            Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
            destination = Path.Combine(destination, relativePath);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
        string download = $"client/browser-files/download?clusterId={cluster}&peerId=target&instanceId={instance:D}&path={Uri.EscapeDataString(destination)}";
        using HttpResponseMessage downloaded = await client.SendAsync(HttpMethod.Get, download).ConfigureAwait(true);
        downloaded.EnsureSuccessStatusCode();
        Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        using HttpResponseMessage stale = await client.SendAsync(HttpMethod.Get, download.Replace(instance.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)).ConfigureAwait(true);
        Assert.False(stale.IsSuccessStatusCode);
        using HttpResponseMessage discarded = await client.SendAsync(HttpMethod.Post, "api/browser-files/discard", stage, cluster, "target").ConfigureAwait(true);
        discarded.EnsureSuccessStatusCode();
        Assert.Empty(Directory.GetDirectories(core.DirectoryPath, ".contents-transfer-*"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(true));
    }
}
