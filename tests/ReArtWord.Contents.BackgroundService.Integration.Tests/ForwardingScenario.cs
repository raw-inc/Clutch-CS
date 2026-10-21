using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>分岐・保持・12要素の反復・スクリプトを含む固定WFを、4つの実Coreとループバックで検証します。</summary>
internal sealed class ForwardingScenario(ITestOutputHelper output)
{
    /// <summary>定義・PC指定・NIC・宛先は試験用コピーにだけ設定します。</summary>
    /// <returns>受信から実UDP送信までの計測と、各入力に対応する出力の確認です。</returns>
    internal async Task RunAsync(string label, int iterations, int? interval = null, int historicalRecords = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath("../../../../../.work/workflow-latency", AppContext.BaseDirectory);
        Directory.CreateDirectory(root);
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_PROBE_CORE");
        CorePeer first = new(cluster, "a", port, true, "host-a", 100, executable);
        CorePeer second = new(cluster, "b", port, false, "host-b", 50, executable);
        CorePeer third = new(cluster, "c", port, false, "host-c", 25, executable);
        CorePeer fourth = new(cluster, "d", port, false, "host-d", 20, executable);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable thirdLifetime = third.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fourthLifetime = fourth.ConfigureAwait(true);
        if (historicalRecords != 0)
        {
            foreach ((CorePeer peer, string id) in new[] { (first, "a"), (second, "b"), (third, "c"), (fourth, "d") })
            {
                await HistoryArchiveForwardingTests.SeedAsync(peer, id, historicalRecords, cancellationToken).ConfigureAwait(false);
            }
        }
        await File.WriteAllTextAsync(root + "/process-probe-directory.txt", first.DirectoryPath, cancellationToken).ConfigureAwait(false);
        await first.StartAsync(cancellationToken).ConfigureAwait(false);
        await second.StartAsync(cancellationToken).ConfigureAwait(false);
        await third.StartAsync(cancellationToken).ConfigureAwait(false);
        await fourth.StartAsync(cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 4, cancellationToken).ConfigureAwait(false);
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Any, 0));
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        int sinkPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        int inputPort = CorePeer.AvailablePort();
        using JsonDocument fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "forwarding-workflow.json"), cancellationToken).ConfigureAwait(false));
        WorkflowDefinition original = fixture.RootElement.Deserialize<WorkflowDefinition>(ContractJson.Options)!;
        string code = "public static string Run() => DateTime.Now.ToString(\"HH:mm:ss.fff\");";
        ScriptAnalysis analysis = await first.GetAsync<ScriptAnalysis>("scripts.analyze", new ScriptAnalysisRequest("csharp", code), cancellationToken).ConfigureAwait(false);
        List<NodeDefinition> nodes = [];
        foreach (NodeDefinition node in original.Nodes)
        {
            JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
            switch (node.Type)
            {
                case "execution.pc.select":
                    string host = "host-a", remote = "send-pc";
                    if (string.Equals(node.Id, remote, StringComparison.Ordinal))
                    {
                        host = "host-b";
                    }
                    settings["hostIds"] = new JsonArray(host);
                    break;
                case "osc.udp.receive":
                    settings["interfaceId"] = first.InterfaceId;
                    settings["localAddress"] = "127.0.0.1";
                    settings["localPort"] = inputPort;
                    break;
                case "osc.udp.send":
                    settings["interfaceId"] = second.InterfaceId;
                    settings["localAddress"] = "127.0.0.1";
                    settings["remoteAddress"] = "127.0.0.1";
                    settings["remotePort"] = sinkPort;
                    settings["localPort"] = 0;
                    break;
                case "value.constant":
                    switch (node.Id)
                    {
                        case "prefix": settings["text"] = "127.0.0."; break;
                        case "remote-port": settings["text"] = sinkPort.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                        case "source-port": settings["text"] = "0"; break;
                    }
                    break;
                case "script.csharp":

                    // 外部から取り込んだコードは実行せず、既知の有限な定数処理に置換します。
                    settings["code"] = code;
                    settings["analysis"] = JsonSerializer.SerializeToNode(analysis, ContractJson.Options);
                    break;
            }
            nodes.Add(node with { Settings = JsonSerializer.SerializeToElement(settings) });
        }

        WorkflowDefinition definition = original with { Nodes = nodes };
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), cancellationToken).ConfigureAwait(false);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved, ContractJson.Options));
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("status").GetProperty("state").GetString() is "listening"), cancellationToken).ConfigureAwait(false);
        JsonElement firstHealth = await first.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false);
        JsonElement secondHealth = await second.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false);
        JsonElement thirdHealth = await third.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false);
        JsonElement fourthHealth = await fourth.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false);
        int[] processIds = [firstHealth.GetProperty("processId").GetInt32(), secondHealth.GetProperty("processId").GetInt32(),
            thirdHealth.GetProperty("processId").GetInt32(), fourthHealth.GetProperty("processId").GetInt32()];
        List<ResourceSample> resources = [CaptureResources("before", processIds)];
        await File.WriteAllTextAsync(root + "/probe-identities.json", JsonSerializer.Serialize(new { first = firstHealth, second = secondHealth }), cancellationToken).ConfigureAwait(false);
        bool delayed = int.TryParse(Environment.GetEnvironmentVariable("CONTENTS_PROBE_START_DELAY_MS"), out int startDelay);
        if (delayed && startDelay is >= 1 and <= 10000)
        {
            await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
        }
        List<LatencySample> measured = [];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (string branch in new[] { "1", "all" })
        {
            if (interval is int spacing)
            {
                measured.AddRange(await MeasurePacedAsync(sender, receiver, inputPort, branch, iterations, spacing, cancellationToken).ConfigureAwait(false));
                resources.Add(CaptureResources(branch + ".forwarded", processIds));
                await File.WriteAllTextAsync(root + "/" + label + "-latency.json", JsonSerializer.Serialize(measured, ContractJson.Options), cancellationToken).ConfigureAwait(false);
                await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sourceQueue").GetProperty("running").GetInt32() == 0, cancellationToken).ConfigureAwait(false);
                resources.Add(CaptureResources(branch + ".idle", processIds));
                continue;
            }
            for (int index = 0; index < iterations; index++)
            {
                string suffix = "probe-" + branch + "-" + index;
                byte[] packet = Packet(branch, index);

                // allでは数値IDの枝は選択されず、反復の12要素だけを送信します。
                int expected = branch switch { "all" => 12, _ => 1 };
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, inputPort), cancellationToken).ConfigureAwait(false);
                double firstMs = 0;
                HashSet<string> outputs = [];
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                for (int count = 0; count < expected; count++)
                {
                    UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (count == 0)
                    {
                        firstMs = milliseconds;
                    }
                    string addressText = Encoding.ASCII.GetString(received.Buffer.AsSpan(0, Array.IndexOf(received.Buffer, (byte)0)));
                    Assert.EndsWith("/" + suffix, addressText, StringComparison.Ordinal);
                    Assert.True(outputs.Add(addressText));
                }
                Assert.Equal(ExpectedAddresses(branch, index), outputs.Order(StringComparer.Ordinal));
                measured.Add(new(branch, index, firstMs, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, outputs.ToArray()));
                await File.WriteAllTextAsync(root + "/" + label + "-latency.json", JsonSerializer.Serialize(measured, ContractJson.Options), cancellationToken).ConfigureAwait(false);
                await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sourceQueue").GetProperty("running").GetInt32() == 0, cancellationToken).ConfigureAwait(false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (IGrouping<string, LatencySample> samples in measured.GroupBy(item => item.Branch))
        {
            double[] warm = samples.Skip(1).Select(item => item.LastMs).Order().ToArray();
            output.WriteLine($"{samples.Key}: first={samples.First().LastMs:F3}ms");
            if (warm.Length != 0)
            {
                output.WriteLine($"{samples.Key}: n={warm.Length}, p50={warm[warm.Length / 2]:F3}ms, p95={warm[(int)Math.Ceiling(warm.Length * 0.95) - 1]:F3}ms, max={warm[^1]:F3}ms");
            }
        }
        JsonElement status = await first.GetAsync<JsonElement>("status", cancellationToken).ConfigureAwait(false);
        if (historicalRecords != 0)
        {
            foreach (CorePeer peer in new[] { first, second, third, fourth })
            {
                Contracts.Persistence.History.HistoryArchiveStatus? historyStatus = null;
                await CorePeer.WaitAsync(async token =>
                {
                    historyStatus = await peer.GetAsync<Contracts.Persistence.History.HistoryArchiveStatus>("history.archive.status", token).ConfigureAwait(false);
                    return historyStatus.Problem is null && 0 < historyStatus.ArchivedRecords;
                }, cancellationToken).ConfigureAwait(false);
                Assert.NotNull(historyStatus);
                Assert.Null(historyStatus.Problem);
                Assert.True(0 < historyStatus.ArchivedRecords);
                output.WriteLine(JsonSerializer.Serialize(historyStatus, ContractJson.Options));
            }
        }
        Assert.Empty(status.GetProperty("sourceFailures").EnumerateObject());
        await File.WriteAllTextAsync(root + "/" + label + "-status.json", status.GetRawText(), cancellationToken).ConfigureAwait(false);
        JsonElement history = await first.GetAsync<JsonElement>("executions.list", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(root + "/" + label + "-history.json", history.GetRawText(), cancellationToken).ConfigureAwait(false);
        resources.Add(CaptureResources("after", processIds));
        await File.WriteAllTextAsync(root + "/" + label + "-resources.json", JsonSerializer.Serialize(resources, ContractJson.Options), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>プロセス終了前にCore自身のCPU時間と保持量を観測し、子workerのCPUや物理書込みとは区別します。</summary>
    /// <returns>この試験が所有する4プロセスの観測値です。</returns>
    private static ResourceSample CaptureResources(string stage, int[] processIds)
    {
        List<ProcessSample> samples = [];
        foreach (int id in processIds)
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
            samples.Add(new(id, process.TotalProcessorTime.TotalMilliseconds, process.PrivateMemorySize64, process.WorkingSet64, process.Threads.Count, process.HandleCount));
        }
        return new(stage, DateTimeOffset.UtcNow, samples);
    }

    /// <summary>通常CIと明示負荷試験で同じ有限回数の指定を使用します。</summary>
    /// <returns>1〜128の明示値、またはその試験の既定値です。</returns>
    internal static int Iterations(int fallback)
    {
        bool configured = int.TryParse(Environment.GetEnvironmentVariable("CONTENTS_PROBE_ITERATIONS"), out int requested);
        if (configured && requested is >= 1 and <= 128)
        {
            return requested;
        }
        return fallback;
    }

    /// <summary>前回WFの終了を待たず定周期で入力し、並行出力を元入力へ照合します。</summary>
    /// <returns>全入力の最初・最後の実受信時間と重複のない送信内容です。</returns>
    private static async Task<IReadOnlyList<LatencySample>> MeasurePacedAsync(UdpClient sender, UdpClient receiver, int port, string branch, int iterations, int interval,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30 + iterations * interval / 1000));
        long[] starts = new long[iterations];
        double[] first = new double[iterations], last = new double[iterations];
        HashSet<string>[] outputs = Enumerable.Range(0, iterations).Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        int expected = branch switch { "all" => 12, _ => 1 };
        Task sending = SendAsync(limit.Token);
        try
        {
            for (int count = 0; count < iterations * expected; count++)
            {
                UdpReceiveResult received = await receiver.ReceiveAsync(limit.Token).ConfigureAwait(false);
                long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
                string address = Encoding.ASCII.GetString(received.Buffer.AsSpan(0, Array.IndexOf(received.Buffer, (byte)0)));
                string marker = "/probe-" + branch + "-";
                int position = address.LastIndexOf(marker, StringComparison.Ordinal);
                Assert.True(0 <= position, address);
                int index = int.Parse(address[(position + marker.Length)..], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(index, 0, iterations - 1);
                long started = Volatile.Read(ref starts[index]);
                Assert.NotEqual(0, started);
                Assert.True(outputs[index].Add(address), address);
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started, arrived).TotalMilliseconds;
                if (outputs[index].Count == 1)
                {
                    first[index] = elapsed;
                }
                last[index] = elapsed;
            }
            await sending.ConfigureAwait(false);
            for (int index = 0; index < iterations; index++)
            {
                Assert.Equal(ExpectedAddresses(branch, index), outputs[index].Order(StringComparer.Ordinal));
            }
            return Enumerable.Range(0, iterations).Select(index => new LatencySample(branch, index, first[index], last[index], outputs[index].ToArray())).ToArray();
        }
        finally
        {
            await limit.CancelAsync().ConfigureAwait(false);
            try
            {
                await sending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                // 試験終了時も、所有する送信作業の終了を待ちます。
            }
        }

        async Task SendAsync(CancellationToken token = default)
        {
            long origin = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int index = 0; index < iterations; index++)
            {
                double wait = index * interval - System.Diagnostics.Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
                if (0 < wait)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                }
                Volatile.Write(ref starts[index], System.Diagnostics.Stopwatch.GetTimestamp());
                await sender.SendAsync(Packet(branch, index), new IPEndPoint(IPAddress.Loopback, port), token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>入力ごとに単発の宛先、または反復の全12宛先を照合します。</summary>
    /// <returns>重複・欠落・別入力との混同を検出するOSCアドレスです。</returns>
    private static IEnumerable<string> ExpectedAddresses(string branch, int index)
    {
        string suffix = "/probe-" + branch + "-" + index;
        const string all = "all";
        if (string.Equals(branch, all, StringComparison.Ordinal))
        {
            return Enumerable.Range(1, 12).Select(item => "/" + item.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + suffix);
        }
        return ["/1" + suffix];
    }

    /// <summary>試験入力を一意なOSCアドレスにし、引数なしの正しいパケットを組み立てます。</summary>
    /// <returns>ループバックへだけ送る試験パケットです。</returns>
    private static byte[] Packet(string branch, int index)
    {
        byte[] address = Encoding.ASCII.GetBytes("/" + branch + "/probe-" + branch + "-" + index);
        byte[] packet = new byte[((address.Length + 4) / 4) * 4 + 4];
        address.CopyTo(packet, 0);
        packet[^4] = (byte)',';
        return packet;
    }

    /// <summary>入力ごとの最初と最後の送信、および確認したOSCアドレスです。</summary>
    private sealed record LatencySample(string Branch, int Index, double FirstMs, double LastMs, string[] Outputs);

    /// <summary>入力前・転送後・実行解放後の観測です。</summary>
    private sealed record ResourceSample(string Stage, DateTimeOffset Time, IReadOnlyList<ProcessSample> Processes);

    /// <summary>CPUは累積Core時間です。外部worker、機器、物理ディスクI/Oを含みません。</summary>
    private sealed record ProcessSample(int Id, double CpuMilliseconds, long PrivateBytes, long WorkingSetBytes, int Threads, int Handles);
}
