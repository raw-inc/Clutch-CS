using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Scripting;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scripting;

/// <summary>継続C#の実worker・実UDP・取消と型付き要素の境界を検証します。</summary>
public sealed class CSharpSourceTests
{
    /// <summary>注入tokenは入力に出さず、yieldの要素型と単発解析を分離します。</summary>
    [Fact]
    public void InfersItemsAndExcludesInjectedCancellation()
    {
        const string code = "public static async IAsyncEnumerable<byte[]> Run(int count = 2, [EnumeratorCancellation] CancellationToken token = default) { await Task.Yield(); yield return new byte[count]; }";
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code, continuous: true);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal("binary", analysis.Contract!.Result.Type);
        Assert.Equal("count", Assert.Single(analysis.Contract.Arguments).Key);
        Assert.True(analysis.Contract.Arguments["count"].Optional);
        NodeDefinition definition = new("source", "script.csharp-source", Settings(code));
        Assert.NotNull(ScriptSettings.Confirmed(definition.Type, definition.Settings));
        NodeDescriptor descriptor = ScriptInputPorts.Resolve(Node().Descriptor, definition);
        Assert.DoesNotContain(descriptor.Inputs, port => port.Name.Contains("token", StringComparison.Ordinal));
        Assert.NotEmpty(CSharpScriptCompiler.Analyze(code).Diagnostics);
        Assert.NotEmpty(CSharpScriptCompiler.Analyze("public static int Run() => 1;", continuous: true).Diagnostics);
    }

    /// <summary>実通信を繰り返して独立した値を通知し、取消でソケットとfinallyを解放します。</summary>
    /// <returns>実UDP受信・正常停止とWF取消の二経路です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivesUdpAndReleasesOnStopAsync(bool cancel)
    {
        const string code = """
            using System.Net;
            using System.Net.Sockets;
            using System.IO;
            public static async IAsyncEnumerable<byte[]> Run(string released, [EnumeratorCancellation] CancellationToken token = default)
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                try
                {
                    yield return BitConverter.GetBytes(((IPEndPoint)udp.Client.LocalEndPoint!).Port);
                    while (!token.IsCancellationRequested)
                    {
                        var packet = await udp.ReceiveAsync(token);
                        yield return packet.Buffer;
                    }
                }
                finally { File.WriteAllText(released, "released"); }
            }
            """;
        using MediaTestFiles files = new();
        string marker = Path.Combine(files.Root, "released.txt");
        using CancellationTokenSource stop = new(), deadline = new(TimeSpan.FromSeconds(30));
        TaskCompletionSource<int> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<NodeEmission> items = new();
        NodeExecutionContext initial = Context(code, new { released = marker });
        NodeSourceContext source = new(initial, () => initial with { ExecutionId = Guid.NewGuid() }, async (item, token) =>
        {
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetResult(BitConverter.ToInt32(item.Result.Outputs["value"].Binary.Span));
            }
            else
            {
                await Task.Delay(25, token).ConfigureAwait(true);
                items.Enqueue(item);
                if (items.Count == 3)
                {
                    delivered.TrySetResult();
                }
            }
        }, () => stop.IsCancellationRequested, stopToken: stop.Token);
        Task<NodeResult> running = Node().RunAsync(source, deadline.Token);
        try
        {
            int port = await ready.Task.WaitAsync(deadline.Token).ConfigureAwait(true);
            using UdpClient sender = new();
            for (int index = 1; index <= 3; index++)
            {
                await sender.SendAsync(new byte[] { (byte)index, 255 }, new IPEndPoint(IPAddress.Loopback, port), deadline.Token).ConfigureAwait(true);
            }
            await delivered.Task.WaitAsync(deadline.Token).ConfigureAwait(true);
            if (cancel)
            {
                await deadline.CancelAsync().ConfigureAwait(true);
            }
            else
            {
                await stop.CancelAsync().ConfigureAwait(true);
            }
            EOutcome expected = cancel ? EOutcome.Cancelled
                                       : EOutcome.Succeeded;
            Assert.Equal(expected, (await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true)).Outcome);
            Assert.Equal("released", await File.ReadAllTextAsync(marker).ConfigureAwait(true));
            Assert.Equal([1, 2, 3], items.Select(item => (int)item.Result.Outputs["value"].Binary.Span[0]));
            Assert.Equal(3, items.Select(item => item.Context.ExecutionId).Distinct().Count());
            using UdpClient reopened = new(new IPEndPoint(IPAddress.Loopback, port));
            Assert.NotNull(reopened.Client.LocalEndPoint);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(true);
            await running.ConfigureAwait(true);
        }
    }

    /// <summary>低速な後続でも順序と値を保持し、有限列挙の終了を確認します。</summary>
    /// <returns>実pipeを流れる大量要素の結果です。</returns>
    [Fact]
    public async Task SlowConsumerReceivesAllItemsWithoutSharedValuesAsync()
    {
        const string code = "public static async IAsyncEnumerable<byte[]> Run(int count = 100) { for (int i=0; i<count; i++) { await Task.Yield(); var bytes = new byte[16384]; bytes[0]=(byte)i; yield return bytes; } }";
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        NodeExecutionContext initial = Context(code, new { count = (int?)null });
        List<byte> values = [];
        NodeSourceContext source = new(initial, () => initial, async (item, token) =>
        {
            await Task.Delay(5, token).ConfigureAwait(true);
            values.Add(item.Result.Outputs["value"].Binary.Span[0]);
        }, () => false);
        Assert.Equal(EOutcome.Succeeded, (await Node().RunAsync(source, deadline.Token).ConfigureAwait(true)).Outcome);
        Assert.Equal(Enumerable.Range(0, 100).Select(value => (byte)value), values);
    }

    /// <summary>コード例外と出力上限を正常な列挙終了へ変換しません。</summary>
    /// <returns>子プロセスの診断と終了結果です。</returns>
    [Theory]
    [InlineData("public static async IAsyncEnumerable<string> Run() { await Task.Yield(); yield return new string('x', 10000); }", 0)]
    [InlineData("public static async IAsyncEnumerable<string> Run() { await Task.Yield(); yield return \"first\"; throw new InvalidOperationException(\"source failure\"); }", 1)]
    public async Task WorkerFailureIsNotSuccessfulCompletionAsync(string code, int expected)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        Observability.RecordingWorkflowLog log = new();
        NodeExecutionContext initial = Context(code, new { }, maximum: 1024) with { Log = log };
        int count = 0;
        NodeSourceContext source = new(initial, () => initial, (_, _) =>
        {
            count++;
            return ValueTask.CompletedTask;
        }, () => false);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            count = 0;
            log.Entries.Clear();
            NodeResult result = await Node().RunAsync(source, deadline.Token).ConfigureAwait(true);
            Assert.True(result.Outcome == EOutcome.Failed, JsonSerializer.Serialize(log.Entries));
            Assert.Equal(expected, count);
            Assert.Equal("node.exception", Assert.Single(log.Entries).Message);
        }
    }

    /// <summary>取消を無視するコードは猶予後に停止し、協調停止成功とは報告しません。</summary>
    /// <returns>実PIDの終了確認と結果不明です。</returns>
    [Fact]
    public async Task UncooperativeWorkerIsStoppedAfterGraceAsync()
    {
        const string code = "public static async IAsyncEnumerable<int> Run() { yield return System.Diagnostics.Process.GetCurrentProcess().Id; await Task.Delay(60000); }";
        using CancellationTokenSource stop = new(), deadline = new(TimeSpan.FromSeconds(20));
        int pid = 0;
        NodeExecutionContext initial = Context(code, new { });
        NodeSourceContext source = new(initial, () => initial, async (item, _) =>
        {
            pid = item.Result.Outputs["value"].Json.GetInt32();
            await stop.CancelAsync().ConfigureAwait(true);
        }, () => stop.IsCancellationRequested, stopToken: stop.Token);
        Assert.Equal(EOutcome.Unknown, (await Node().RunAsync(source, deadline.Token).ConfigureAwait(true)).Outcome);
        Assert.True(0 < pid);
        Assert.DoesNotContain(Process.GetProcessesByName("Clutch-CS.CoreService"), process =>
        {
            using (process)
            {
                return process.Id == pid && !process.HasExited;
            }
        });
    }

    /// <summary>テスト出力先の実Coreを専用workerに使用します。</summary>
    /// <returns>通常登録された継続ノードです。</returns>
    internal static ISourceNode Node(Nodes.Scripting.Packages.ScriptPackageStore? packages = null)
    {
        string name = "Clutch-CS.CoreService";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        return (ISourceNode)ScriptingNodeRegistration.Create(new(Path.Combine(AppContext.BaseDirectory, name), []), packages).Single(node => node.Descriptor.Type is "script.csharp-source");
    }

    /// <summary>編集時と同じ解析を保存し、固定値を名前付き引数として渡します。</summary>
    /// <returns>型検証済みの初期文脈です。</returns>
    private static NodeExecutionContext Context(string code, object arguments, int maximum = 1048576)
        => NodeTestContext.Create(Settings(code, maximum).GetRawText(), extra: new Dictionary<string, NodeValue> { ["arguments"] = NodeValue.FromObject(arguments) });

    /// <summary>単発解析と混同しない保存設定を生成します。</summary>
    /// <returns>継続実行用の確認済み設定です。</returns>
    private static JsonElement Settings(string code, int maximum = 1048576)
    {
        ScriptAnalysis analysis = CSharpScriptCompiler.Analyze(code, continuous: true);
        Assert.Empty(analysis.Diagnostics);
        return JsonSerializer.SerializeToElement(new { code, analysis, maxBytes = maximum }, ContractJson.Options);
    }
}
