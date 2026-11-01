using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.SignalScope;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Terminal;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreで三機能へ不正な接続を送り、同じ実体で管理と共有保存を継続できることを確認します。</summary>
public sealed class SessionFailureProcessTests
{
    /// <summary>認証後の正常切断は警告せず、同じCoreで各機能へ再接続できます。</summary>
    /// <returns>画面取得・入力・パケットキャプチャを開始せず、実workerまで含む終了確認です。</returns>
    // 補助プロセスが接続時に音声も初期化するため、画面・音声の実機条件を適用します。
    [HostedDesktopFact]
    public async Task NormalSessionEndsDoNotLogFailuresAndAllowReconnectAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string cluster = "session-close-" + Guid.NewGuid().ToString("N");
        CorePeer peer = new(cluster, "first", CorePeer.AvailablePort(), true, executablePath: PrepareApplication(cluster));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await peer.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("health", token).ConfigureAwait(false)).GetProperty("ready").GetBoolean(), timeout.Token).ConfigureAwait(true);
        JsonElement before = await peer.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        TerminalRequest create = await TerminalTestRequest.CreateAsync(peer, cancellationToken: timeout.Token).ConfigureAwait(true);
        _ = await peer.PostAsync<TerminalView>("terminal.create", create, timeout.Token).ConfigureAwait(true);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            DesktopTicket desktop = await peer.NotifyAsync<DesktopTicket>("desktop.open", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
            await CloseConnectionAsync(desktop.Port, desktop.Token, "desktop", timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => await peer.GetAsync<int>("desktop.status", token).ConfigureAwait(false) == 0, timeout.Token).ConfigureAwait(true);
            SignalScopeTicket capture = await peer.NotifyAsync<SignalScopeTicket>("signal-scope.open", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
            await CloseConnectionAsync(capture.Port, capture.Token, "capture", timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => await peer.GetAsync<int>("signal-scope.status", token).ConfigureAwait(false) == 0, timeout.Token).ConfigureAwait(true);
            TerminalTicket terminal = await peer.NotifyAsync<TerminalTicket>("terminal.attach", new TerminalAttachRequest(create.SessionId, create.Owner, Guid.NewGuid()), timeout.Token).ConfigureAwait(true);
            await CloseConnectionAsync(terminal.Port, terminal.Token, "terminal", timeout.Token).ConfigureAwait(true);
            Assert.False(Assert.Single(await peer.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true)).Exited);
        }
        Assert.True(await peer.PostAsync<bool>("terminal.close", create, timeout.Token).ConfigureAwait(true));
        Assert.Empty((await peer.GetAsync<LogQueryResult>("logs.query", new LogQuery(Text: "Session.Failed"), timeout.Token).ConfigureAwait(true)).Entries);
        JsonElement after = await peer.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        Assert.True(after.GetProperty("ready").GetBoolean());
        Assert.Equal(before.GetProperty("instanceId").GetGuid(), after.GetProperty("instanceId").GetGuid());
    }

    /// <summary>認証後の応答を確認し、TCPの送信側だけを閉じて相手の解放完了まで読み取ります。</summary>
    /// <returns>実通信の正常FINと、相手側のEOFです。</returns>
    private static async Task CloseConnectionAsync(int port, string token, string protocol, CancellationToken cancellationToken = default)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString(token), cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[262144];
        switch (protocol)
        {
            case "desktop":
                await stream.ReadExactlyAsync(buffer.AsMemory(0, DesktopWire.HeaderSize), cancellationToken).ConfigureAwait(false);
                int length = DesktopWire.PayloadLength(buffer.AsSpan(0, DesktopWire.HeaderSize));
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                break;
            case "terminal":
                _ = await TerminalWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                break;
            case "capture":
                await stream.WriteAsync(SignalScopeWire.Control(JsonSerializer.SerializeToElement(new { type = "list" })), cancellationToken).ConfigureAwait(false);
                _ = await SignalScopeWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                break;
            default: throw new ArgumentException("Unexpected test protocol.", nameof(protocol));
        }
        client.Client.Shutdown(SocketShutdown.Send);
        using CancellationTokenSource closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closing.CancelAfter(TimeSpan.FromSeconds(8));
        await stream.CopyToAsync(Stream.Null, closing.Token).ConfigureAwait(false);
    }

    /// <summary>接続拒否、壊れたフレーム、再接続、診断とクラスタ参加を実プロセスで検証します。</summary>
    /// <returns>外部NICのキャプチャや画面取得を行わない、二つのCoreの生存確認です。</returns>
    [WindowsFact]
    public async Task InvalidSessionsDoNotStopCoreOrPreventNewConnections()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string cluster = "session-failure-" + Guid.NewGuid().ToString("N");
        string executable = PrepareApplication(cluster);
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "first", port, true, executablePath: executable);
        CorePeer second = new(cluster, "second", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await second.GetAsync<JsonElement>("health", token).ConfigureAwait(false)).GetProperty("ready").GetBoolean(), timeout.Token).ConfigureAwait(true);
        JsonElement before = await first.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            DesktopTicket desktop = await first.NotifyAsync<DesktopTicket>("desktop.open", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
            await RejectAsync(desktop.Port, null, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => await first.GetAsync<int>("desktop.status", token).ConfigureAwait(false) == 0, timeout.Token).ConfigureAwait(true);
            SignalScopeTicket capture = await first.NotifyAsync<SignalScopeTicket>("signal-scope.open", Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
            string? tokenValue = null;
            if (attempt != 0)
            {
                tokenValue = capture.Token;
            }
            await RejectAsync(capture.Port, tokenValue, timeout.Token).ConfigureAwait(true);
            await CorePeer.WaitAsync(async token => await first.GetAsync<int>("signal-scope.status", token).ConfigureAwait(false) == 0, timeout.Token).ConfigureAwait(true);
        }
        TerminalRequest create = await TerminalTestRequest.CreateAsync(first, cancellationToken: timeout.Token).ConfigureAwait(true);
        TerminalView terminal = await first.PostAsync<TerminalView>("terminal.create", create, timeout.Token).ConfigureAwait(true);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            TerminalTicket ticket = await first.NotifyAsync<TerminalTicket>("terminal.attach", new TerminalAttachRequest(create.SessionId, create.Owner, Guid.NewGuid()), timeout.Token).ConfigureAwait(true);
            string? tokenValue = null;
            if (attempt != 0)
            {
                tokenValue = ticket.Token;
            }
            await RejectAsync(ticket.Port, tokenValue, timeout.Token).ConfigureAwait(true);
            Assert.Equal(terminal.SessionId, Assert.Single(await first.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true)).SessionId);
        }
        TerminalView entered = await first.NotifyAsync<TerminalView>("terminal.input", create with { Data = Convert.ToBase64String("\r"u8.ToArray()) }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, entered.Sequence);
        Assert.True(await first.PostAsync<bool>("terminal.close", create, timeout.Token).ConfigureAwait(true));
        Assert.Empty(await first.GetAsync<TerminalView[]>("terminal.list", timeout.Token).ConfigureAwait(true));
        LogQueryResult? logs = null;
        await CorePeer.WaitAsync(async token =>
        {
            logs = await first.GetAsync<LogQueryResult>("logs.query", new LogQuery(Text: "Session.Failed"), token).ConfigureAwait(false);
            return 6 <= logs.Entries.Count;
        }, timeout.Token).ConfigureAwait(true);
        Assert.All(logs!.Entries, entry => Assert.Equal("InvalidDataException", entry.Data!.Json.GetProperty("exceptionType").GetString()));
        Assert.Equal(["RemoteDesktop", "SignalScope", "Terminal"], logs.Entries.Select(entry => entry.Data!.Json.GetProperty("component").GetString()).Distinct().Order(StringComparer.Ordinal));
        var target = new { workflowId = "session-test", scope = "shared", key = "continued" };
        JsonElement written = await first.PostAsync<JsonElement>("internal-data.compare-exchange", new
        {
            operationId = Guid.NewGuid(), target, expectedRevision = (string?)null, value = NodeValue.FromObject(42),
        }, timeout.Token).ConfigureAwait(true);
        Assert.True(written.GetProperty("result").GetProperty("applied").GetBoolean());
        JsonElement read = await second.GetAsync<JsonElement>("internal-data.read", target, timeout.Token).ConfigureAwait(true);
        Assert.Equal(42, read.GetProperty("confirmedValue").GetProperty("json").GetInt32());
        JsonElement after = await first.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true);
        Assert.True(after.GetProperty("ready").GetBoolean());
        Assert.Equal(before.GetProperty("instanceId").GetGuid(), after.GetProperty("instanceId").GetGuid());
        Assert.Equal(before.GetProperty("processId").GetInt32(), after.GetProperty("processId").GetInt32());
    }

    /// <summary>許可値の不一致、または認証後の負のフレーム長を一接続だけへ送ります。</summary>
    /// <returns>当該接続の終了です。</returns>
    private static async Task RejectAsync(int port, string? token, CancellationToken cancellationToken = default)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        byte[] ticket = new byte[32];
        if (token is not null)
        {
            ticket = Convert.FromHexString(token);
        }
        await stream.WriteAsync(ticket, cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            await stream.WriteAsync(new byte[] { 255, 255, 255, 255 }, cancellationToken).ConfigureAwait(false);
        }
        byte[] buffer = new byte[4096];
        int total = 0, count;
        while (0 < (count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)))
        {
            total += count;
            Assert.InRange(total, 0, 65536);
        }
    }

    /// <summary>他fixtureの配布物を変更せず、キャプチャ用workerもある隔離アプリを用意します。</summary>
    /// <returns>この試験だけで使用するCore実行ファイルです。</returns>
    private static string PrepareApplication(string cluster)
    {
        string root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string source = Path.Combine(root, "src", "ReArtWord.Contents.BackgroundService.CoreService", "bin", configuration, "net10.0");
        string destination = Path.Combine(root, ".work", "integration", cluster, "application");
        string[] runtimePrefixes = ["win-", "linux-", "osx-"];
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (runtimePrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        string worker = Path.Combine(destination, "tools", "signal-capture");
        Directory.CreateDirectory(worker);
        string capture = Path.Combine(root, "src", "ReArtWord.Contents.BackgroundService.SignalCapture", "bin", configuration, "net10.0");
        foreach (string file in Directory.EnumerateFiles(capture))
        {
            File.Copy(file, Path.Combine(worker, Path.GetFileName(file)), overwrite: true);
        }
        return Path.Combine(destination, "Clutch-CS.CoreService.exe");
    }
}
