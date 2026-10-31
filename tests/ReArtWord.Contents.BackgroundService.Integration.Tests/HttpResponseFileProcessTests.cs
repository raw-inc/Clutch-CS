using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>HTTPで受け取った画像を別Coreで解析・保存し、元バイトと応答情報を確認します。</summary>
public sealed class HttpResponseFileProcessTests
{
    /// <summary>直接本文とenvelope解析の両経路で、PNGを一回のGETから保存します。</summary>
    /// <returns>実Core二つとHTTPソケットを通したファイル内容です。</returns>
    [Fact]
    public async Task SavesOriginalPngAndMetadataOnAnotherPeerAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
        string directory = Path.Combine(second.DirectoryPath, "content");
        Directory.CreateDirectory(directory);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        NetworkInterface nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback)));
        string url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/image";
        JsonElement settings = JsonSerializer.SerializeToElement(new { });
        TargetDefinition target = new(EExecutionScope.One, ["b"]);
        WorkflowDefinition definition = new("download", "Download image", [
            new("get", "http.request", JsonSerializer.SerializeToElement(new { interfaceId = nic.Id, localAddress = "127.0.0.1", url, method = "GET", authentication = "none", responseBodyFormat = "auto" }), Target: new(EExecutionScope.One, ["a"])),
            new("parse", "http.response.parse", settings, Target: target),
            new("direct", "file.create", JsonSerializer.SerializeToElement(new { path = Path.Combine(directory, "direct.png") }), Target: target),
            new("parsed", "file.create", JsonSerializer.SerializeToElement(new { path = Path.Combine(directory, "parsed.png") }), Target: target),
            new("mime", "file.create", JsonSerializer.SerializeToElement(new { path = Path.Combine(directory, "mime.txt") }), Target: target),
            new("status", "file.create", JsonSerializer.SerializeToElement(new { path = Path.Combine(directory, "status.txt") }), Target: target)],
            [new("get", "value", "parse", "value"), new("get", "body", "direct", "value"), new("parse", "value", "parsed", "value"),
                new("parse", "contentType", "mime", "value"), new("parse", "statusCode", "status", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision), timeout.Token).ConfigureAwait(true);
        using TcpClient accepted = await server.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(true);
        using NetworkStream stream = accepted.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true);
        }
        while (!string.IsNullOrEmpty(line));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n")), timeout.Token).ConfigureAwait(true);
        await stream.WriteAsync(png, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        Assert.True(run.Outcome is EOutcome.Succeeded, JsonSerializer.Serialize(run));
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(directory, "direct.png")).ConfigureAwait(true));
        Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(directory, "parsed.png")).ConfigureAwait(true));
        Assert.Equal("image/png", await File.ReadAllTextAsync(Path.Combine(directory, "mime.txt")).ConfigureAwait(true));
        Assert.Equal("200", await File.ReadAllTextAsync(Path.Combine(directory, "status.txt")).ConfigureAwait(true));
        Assert.False(server.Pending());
    }
}
