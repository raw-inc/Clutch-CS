using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Core二台と実ClientHostでキャッシュ・端末・転送の所有境界を確認します。</summary>
public sealed class RemoteToolsTests
{
    /// <summary>起動設定の文字列と二つの画像が全画面向けの同一originで配信されます。</summary>
    /// <returns>実HTTPの設定と画像本文を確認します。</returns>
    [Fact]
    public async Task BrandingServesOnlyTheConfiguredImages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-branding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aS1sAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(Path.Combine(directory, "logo.png"), png).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "branding.json"), JsonSerializer.Serialize(new ClientBranding("Exhibition", "logo.png", "logo.png"), ContractJson.Options)).ConfigureAwait(true);
        ClientHostProcess client = new(directory);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        ClientBranding branding = (await ReadAsync<ClientBranding>(client, "client/branding").ConfigureAwait(true))!;
        Assert.Equal("Exhibition", branding.ContentsName);
        Assert.StartsWith("/client/branding/header?v=", branding.HeaderLogo, StringComparison.Ordinal);
        Assert.StartsWith("/client/branding/contents?v=", branding.ContentsLogo, StringComparison.Ordinal);
        Assert.Equal(branding.HeaderLogo, branding.Favicon);
        using HttpResponseMessage image = await client.SendAsync(HttpMethod.Get, branding.HeaderLogo!).ConfigureAwait(true);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
        using HttpResponseMessage unconfigured = await client.SendAsync(HttpMethod.Get, "client/branding/other").ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unconfigured.StatusCode);
    }

    /// <summary>別クラスタ間のコピーと追加キューがHTTP画面の寿命から独立して完了します。</summary>
    /// <returns>実通信と実ファイル内容の検証です。</returns>
    [Fact]
    public async Task DashboardCacheAndCopyQueueSurviveIndependentRequests()
    {
        string clusterA = Guid.NewGuid().ToString("N"), clusterB = Guid.NewGuid().ToString("N");
        int[] ports = CorePeer.AvailableUdpPorts(2);
        int portA = ports[0], portB = ports[1];
        CorePeer first = new(clusterA, "a", portA, true), second = new(clusterB, "b", portB, true);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        ClientHostProcess reader = new(Path.Combine(first.DirectoryPath, "another-client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = ((IAsyncDisposable)reader).ConfigureAwait(true);
        string source = Path.Combine(first.DirectoryPath, "content", "tree");
        string destination = Path.Combine(second.DirectoryPath, "content");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(destination);
        byte[] bytes = Enumerable.Range(0, 160000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "sample.bin"), bytes).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(first.DirectoryPath, "content", "next.txt"), "queued separately").ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await client.StartAsync().ConfigureAwait(true);
        await reader.StartAsync().ConfigureAwait(true);
        using (HttpResponseMessage networks = await reader.SendAsync(HttpMethod.Put, "client/networks", new[]
        {
            new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", portA),
            new ClientNetworkBinding(second.InterfaceId, "127.0.0.1", "127.255.255.255", portB),
        }).ConfigureAwait(true))
        {
            networks.EnsureSuccessStatusCode();
        }
        using (HttpResponseMessage networks = await client.SendAsync(HttpMethod.Put, "client/networks", new[]
        {
            new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", portA),
            new ClientNetworkBinding(second.InterfaceId, "127.0.0.1", "127.255.255.255", portB),
}).ConfigureAwait(true))
        {
            networks.EnsureSuccessStatusCode();
        }
        DashboardPeerSnapshot? snapshot = null, other = null;
        await CorePeer.WaitAsync(async token =>
        {
            snapshot = await ReadAsync<DashboardPeerSnapshot>(client, "client/dashboard", clusterA, "a", cancellationToken: token).ConfigureAwait(true);
            other = await ReadAsync<DashboardPeerSnapshot>(client, "client/dashboard", clusterB, "b", cancellationToken: token).ConfigureAwait(true);
            return 2 <= snapshot?.Samples.Count && snapshot.Status is not null && other?.Status is not null;
        }).ConfigureAwait(true);
        DashboardPeerSnapshot reloaded = (await ReadAsync<DashboardPeerSnapshot>(client, "client/dashboard", clusterA, "a").ConfigureAwait(true))!;
        Assert.Contains(reloaded.Samples, sample => sample.Id == snapshot!.Samples[0].Id);
        Assert.NotNull(reloaded.Health);
        DashboardPeerSnapshot delta = (await ReadAsync<DashboardPeerSnapshot>(client, "client/dashboard?after=" + reloaded.Samples[^1].Id.ToString("D"), clusterA, "a").ConfigureAwait(true))!;
        Assert.DoesNotContain(delta.Samples, sample => reloaded.Samples.Any(previous => previous.Id == sample.Id));
        Assert.NotNull(delta.Health);
        Assert.Equal(reloaded.Peer.InstanceId, delta.Peer.InstanceId);
        FileTransferPeer from = new(clusterA, "a", snapshot!.Peer.InstanceId), to = new(clusterB, "b", other!.Peer.InstanceId);
        FileTransferRequest request = new(Guid.NewGuid(), from, to, source, destination);
        FileTransferJob job = (await ReadAsync<FileTransferJob>(client, "client/transfers", body: request).ConfigureAwait(true))!;
        FileTransferJob repeated = (await ReadAsync<FileTransferJob>(client, "client/transfers", body: request).ConfigureAwait(true))!;
        Assert.Equal(job.Request.Id, repeated.Request.Id);
        FileTransferRequest next = new(Guid.NewGuid(), from, to, Path.Combine(first.DirectoryPath, "content", "next.txt"), destination);
        _ = await ReadAsync<FileTransferJob>(client, "client/transfers", body: next).ConfigureAwait(true);
        await client.StopAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(_ => Task.FromResult(File.Exists(Path.Combine(destination, "next.txt")))).ConfigureAwait(true);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "tree", "sample.bin")).ConfigureAwait(true));
        Assert.True(Directory.Exists(Path.Combine(destination, "tree", "empty")));
        FileTransferJob[] jobs = [];
        await CorePeer.WaitAsync(async token =>
        {
            jobs = (await ReadAsync<FileTransferJob[]>(reader, "client/transfers", cancellationToken: token).ConfigureAwait(true))!;
            return jobs.Length == 2 && jobs.All(item => item.State is "Succeeded");
        }).ConfigureAwait(true);
        Assert.Equal(2, jobs.Length);
        Assert.All(jobs, item => Assert.Equal("Succeeded", item.State));
        FileTransferRequest conflict = request with
        {
            Id = Guid.NewGuid()
        };
        _ = await ReadAsync<FileTransferJob>(reader, "client/transfers", body: conflict).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            FileTransferJob[] current = (await ReadAsync<FileTransferJob[]>(reader, "client/transfers", cancellationToken: token).ConfigureAwait(true))!;
            return current.Any(item => item.Request.Id == conflict.Id && item.State is "Failed" or "Unknown");
        }).ConfigureAwait(true);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "tree", "sample.bin")).ConfigureAwait(true));
    }

    /// <summary>二つの画面Hostから端末を引き継ぎ、旧所有者の入力と重複入力を拒否します。</summary>
    /// <returns>実PTYの副作用と明示終了の確認です。</returns>
    [Fact]
    public async Task TerminalCanBeTakenOverWithoutDuplicatingInput()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer core = new(cluster, "a", port, true);
        ClientHostProcess first = new(Path.Combine(core.DirectoryPath, "client-a")), second = new(Path.Combine(core.DirectoryPath, "client-b"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable coreLifetime = ((IAsyncDisposable)core).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = ((IAsyncDisposable)first).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = ((IAsyncDisposable)second).ConfigureAwait(true);
        await core.StartAsync().ConfigureAwait(true);
        foreach (ClientHostProcess client in new[]
{
first, second
})
        {
            await client.StartAsync().ConfigureAwait(true);
            using HttpResponseMessage network = await client.SendAsync(HttpMethod.Post, "client/network", new ClientNetworkBinding(core.InterfaceId, "127.0.0.1", "127.255.255.255", port)).ConfigureAwait(true);
            network.EnsureSuccessStatusCode();
            await CorePeer.WaitAsync(async token => (await ReadAsync<DiscoveredCluster[]>(client, "client/clusters", cancellationToken: token).ConfigureAwait(true))!.Any(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal))).ConfigureAwait(true);
        }
        TerminalRequest create = await TerminalTestRequest.CreateAsync(core).ConfigureAwait(true);
        TerminalView created = (await ReadAsync<TerminalView>(first, "api/terminal/create", cluster, "a", create).ConfigureAwait(true))!;
        Assert.Equal(create.Elevated, created.Elevated);
        Assert.Single((await ReadAsync<TerminalView[]>(second, "api/terminal/sessions", cluster, "a").ConfigureAwait(true))!);
        Guid newOwner = Guid.NewGuid();
        _ = await ReadAsync<TerminalView>(second, "api/terminal/claim", cluster, "a", create with
        {
            Owner = newOwner
        }).ConfigureAwait(true);
        using (HttpResponseMessage stale = await first.SendAsync(HttpMethod.Post, "api/terminal/input", create with
        {
            Data = "DQ=="
        }, cluster, "a", Guid.NewGuid()).ConfigureAwait(true))
        {
            Assert.False(stale.IsSuccessStatusCode);
        }
        string output = Path.Combine(core.DirectoryPath, "terminal-output.txt");
        string identityOutput = Path.Combine(core.DirectoryPath, "terminal-identity.txt");
        string command = $"printf 'once\\n' >> '{output}'\r";
        if (OperatingSystem.IsWindows())
        {
            command = $"[IO.File]::WriteAllText('{identityOutput}', ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)).ToString()); [IO.File]::AppendAllText('{output}', 'once' + [Environment]::NewLine)\r";
        }
        TerminalRequest input = create with
        {
            Owner = newOwner,
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(command))
        };
        _ = await ReadAsync<TerminalView>(second, "api/terminal/input", cluster, "a", input).ConfigureAwait(true);
        _ = await ReadAsync<TerminalView>(second, "api/terminal/input", cluster, "a", input).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => !string.IsNullOrEmpty(await FileObservation.ReadFirstAppendAsync(output, token).ConfigureAwait(true))).ConfigureAwait(true);
        Assert.Equal(["once"], await File.ReadAllLinesAsync(output).ConfigureAwait(true));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(create.Elevated.ToString(), await File.ReadAllTextAsync(identityOutput).ConfigureAwait(true));
        }
        TerminalOutput captured = (await ReadAsync<TerminalOutput>(second, "api/terminal/read", cluster, "a", new TerminalRequest(created.SessionId)).ConfigureAwait(true))!;
        Assert.NotEmpty(captured.Data);
        _ = await ReadAsync<bool>(second, "api/terminal/close", cluster, "a", new TerminalRequest(created.SessionId)).ConfigureAwait(true);
        Assert.Empty((await ReadAsync<TerminalView[]>(first, "api/terminal/sessions", cluster, "a").ConfigureAwait(true))!);
    }

    /// <summary>独立したHTTP要求で画面の再読み込みを再現します。</summary>
    /// <returns>型付きの確認結果です。</returns>
    private static async Task<T?> ReadAsync<T>(ClientHostProcess client, string path, string? cluster = null, string? peer = null, object? body = null, CancellationToken cancellationToken = default)
    {
        HttpMethod method = HttpMethod.Get;
        if (body is not null)
        {
            method = HttpMethod.Post;
        }
        using HttpResponseMessage response = await client.SendAsync(method, path, body, cluster, peer, Guid.NewGuid(), cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(content, ContractJson.Options);
    }
}
