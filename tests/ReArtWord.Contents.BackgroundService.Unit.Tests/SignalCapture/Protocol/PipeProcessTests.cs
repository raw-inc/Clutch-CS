using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol.Tests;

/// <summary>通常 DLL と NativeAOT の実プロセスで、バイナリ契約と終了処理を検証します。</summary>
public sealed class PipeProcessTests
{
    /// <summary>稼働中のUDP除外と解除がAOTのJSON経路でも有効です。</summary>
    /// <returns>除外置換のプロセス試験。</returns>
    [Fact]
    public async Task ReplacesUdpExclusionsWhileRunningAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var client = new PipeTestClient();
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"exclusions\",\"excludedEndpoints\":[{\"transport\":\"udp\",\"address\":\"127.0.0.2\",\"port\":9000},{\"transport\":\"udp\",\"address\":\"127.0.0.2\",\"port\":9001}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("exclusions", timeout.Token).ConfigureAwait(true);
        int packets = 0;
        while (packets < 3)
        {
            PipeFrame frame = await client.ReadAsync(timeout.Token).ConfigureAwait(true);
            if (frame.Kind != 2)
            {
                continue;
            }
            int length = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
            using JsonDocument metadata = JsonDocument.Parse(frame.Payload.AsMemory(4, length));
            Assert.Equal("TCP", metadata.RootElement.GetProperty("transport").GetString());
            packets++;
        }
        await client.SendAsync("{\"type\":\"exclusions\"}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("exclusions", timeout.Token).ConfigureAwait(true);
        bool udp = false;
        while (!udp)
        {
            PipeFrame frame = await client.ReadAsync(timeout.Token).ConfigureAwait(true);
            if (frame.Kind == 2)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
                using JsonDocument metadata = JsonDocument.Parse(frame.Payload.AsMemory(4, length));
                udp = string.Equals("UDP", metadata.RootElement.GetProperty("transport").GetString(), StringComparison.Ordinal);
            }
        }
        client.CloseInput();
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>読み手が遅い場合の欠落を通知し、停止後の再開では統計を初期化します。</summary>
    /// <returns>過負荷と再開のプロセス試験。</returns>
    [Fact]
    public async Task ReportsOverloadAndRestartsWithResetCountersAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new PipeTestClient(demoRate: 20000);
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
        await Task.Delay(400, timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"stop\"}", timeout.Token).ConfigureAwait(true);
        JsonElement statistics = await client.ReadEventAsync("stats", timeout.Token).ConfigureAwait(true);
        long previousReceived = statistics.GetProperty("received").GetInt64();
        Assert.True(512 < previousReceived);
        Assert.True(0 < statistics.GetProperty("dropped").GetInt64());
        Assert.Equal(0, statistics.GetProperty("suppressed").GetInt64());
        Assert.False((await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true)).GetProperty("running").GetBoolean());
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        Assert.True((await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true)).GetProperty("running").GetBoolean());
        await client.SendAsync("{\"type\":\"stop\"}", timeout.Token).ConfigureAwait(true);
        statistics = await client.ReadEventAsync("stats", timeout.Token).ConfigureAwait(true);
        Assert.True(statistics.GetProperty("received").GetInt64() < previousReceived);
        client.CloseInput();
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>hello / catalog / start / raw / stop / EOF の往復を行います。</summary>
    /// <returns>プロセス試験。</returns>
    [Fact]
    public async Task StreamsDemoAndStopsWithoutPacketsAfterAcknowledgementAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var client = new PipeTestClient();
        JsonElement hello = await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, hello.GetProperty("protocolVersion").GetInt32());
        await client.SendAsync("{\"type\":\"list\"}", timeout.Token).ConfigureAwait(true);
        JsonElement catalog = await client.ReadEventAsync("catalog", timeout.Token).ConfigureAwait(true);
        JsonElement adapter = Assert.Single(catalog.GetProperty("adapters").EnumerateArray());
        Assert.Equal("demo", adapter.GetProperty("backend").GetString());
        Assert.Equal("demo0", adapter.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("interfaceId").ValueKind);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        Assert.True((await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true)).GetProperty("running").GetBoolean());
        var protocols = new HashSet<string>(StringComparer.Ordinal);
        while (protocols.Count < 3)
        {
            PipeFrame frame = await client.ReadAsync(timeout.Token).ConfigureAwait(true);
            if (frame.Kind != 2)
            {
                continue;
            }
            int jsonLength = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
            Assert.InRange(jsonLength, 1, frame.Payload.Length - 5);
            using JsonDocument metadata = JsonDocument.Parse(frame.Payload.AsMemory(4, jsonLength));
            JsonElement packet = metadata.RootElement;
            Assert.Equal("demo:demo0", packet.GetProperty("sourceId").GetString());
            Assert.Equal("demo:demo0", packet.GetProperty("observationPoint").GetString());
            Assert.EndsWith("Z", packet.GetProperty("time").GetString(), StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", packet.GetProperty("fingerprint").GetString()!);
            Assert.InRange(frame.Payload.Length - 4 - jsonLength, 1, 9000);
            Assert.InRange(packet.GetProperty("details").GetString()!.Length, 0, 16384);
            protocols.Add(packet.GetProperty("protocol").GetString()!);
        }

        Assert.Contains("OSC", protocols);
        Assert.Contains("UDP", protocols);
        Assert.Contains("HTTP", protocols);
        await client.SendAsync("{\"type\":\"stop\"}", timeout.Token).ConfigureAwait(true);
        JsonElement state;
        bool running;
        do
        {
            state = await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
            running = state.GetProperty("running").GetBoolean();
        }
        while (running);
        await client.SendAsync("{\"type\":\"list\"}", timeout.Token).ConfigureAwait(true);
        PipeFrame next = await client.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(1, next.Kind);
        client.CloseInput();
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
        Assert.Equal("", await client.ErrorAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>解析前の TCP endpoint 除外を実プロセスで確認します。</summary>
    /// <returns>プロセス試験。</returns>
    [Fact]
    public async Task SuppressesTcpWithoutSuppressingUdpAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new PipeTestClient();
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}],\"excludedTcpEndpoints\":[{\"address\":\"127.0.0.2\",\"port\":80}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
        int packets = 0;
        bool observedSuppression = false;
        while (!observedSuppression)
        {
            PipeFrame frame = await client.ReadAsync(timeout.Token).ConfigureAwait(true);
            if (frame.Kind == 2)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(frame.Payload);
                using JsonDocument metadata = JsonDocument.Parse(frame.Payload.AsMemory(4, length));
                Assert.Equal("UDP", metadata.RootElement.GetProperty("transport").GetString());
                packets++;
                continue;
            }

            using JsonDocument control = JsonDocument.Parse(frame.Payload);
            observedSuppression = control.RootElement.TryGetProperty("suppressed", out JsonElement suppressed) && 0 < suppressed.GetInt64();
        }
        Assert.True(0 < packets);
        client.CloseInput();
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>入力 EOF と出力切断の両方で取得中の worker が終了します。</summary>
    /// <returns>プロセス試験。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitsOnPipeClosureAsync(bool closeOutput)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new PipeTestClient();
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
        if (closeOutput)
        {
            client.CloseOutput();
        }
        else
        {
            client.CloseInput();
        }
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>不正フレームの内容を標準エラーへ漏らしません。</summary>
    /// <returns>プロセス試験。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(2097153)]
    [InlineData(100)]
    public async Task RejectsInvalidOrIncompleteFramesAsync(int length)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new PipeTestClient();
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await client.Input.WriteAsync(header, timeout.Token).ConfigureAwait(true);
        client.CloseInput();
        JsonElement error = await client.ReadEventAsync("error", timeout.Token).ConfigureAwait(true);
        Assert.Equal("Invalid or incomplete pipe frame.", error.GetProperty("message").GetString());
        Assert.Equal(1, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
        Assert.Equal("", await client.ErrorAsync(timeout.Token).ConfigureAwait(true));
    }

    /// <summary>遅い読み手が出力を完全に止めても、終了待ちが無期限になりません。</summary>
    /// <returns>プロセス試験。</returns>
    [Fact]
    public async Task ExitsWhenReaderStopsConsumingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var client = new PipeTestClient();
        await client.ReadEventAsync("hello", timeout.Token).ConfigureAwait(true);
        await client.SendAsync("{\"type\":\"start\",\"sources\":[{\"backend\":\"demo\",\"adapterId\":\"demo0\"}]}", timeout.Token).ConfigureAwait(true);
        await client.ReadEventAsync("state", timeout.Token).ConfigureAwait(true);
        Assert.Equal(0, await client.ExitAsync(timeout.Token).ConfigureAwait(true));
    }
}
