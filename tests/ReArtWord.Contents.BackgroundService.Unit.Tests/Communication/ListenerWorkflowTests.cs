using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Communication;

/// <summary>設定ポートの型解決と、待受から独立した下流の並行実行を確認します。</summary>
public sealed class ListenerWorkflowTests
{
    /// <summary>一件目の下流を保留しても、同じSourceの次のOSCから別の実行が始まります。</summary>
    /// <returns>整数・配列・範囲を配線した実OSC通信による検証です。</returns>
    [Theory]
    [InlineData("integer", false)]
    [InlineData("array", false)]
    [InlineData("string", false)]
    [InlineData("integer", true)]
    [InlineData("array", true)]
    [InlineData("string", true)]
    public async Task ConnectedPortSelectionKeepsReceivingWhileDownstreamRuns(string inputType, bool tcp)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        int[] ports = MultiPortListenerTests.AvailablePair(tcp);
        string type = tcp ? "osc.tcp.listen"
                          : "osc.udp.receive";
        JsonElement portValue = inputType switch
        {
            "integer" => JsonSerializer.SerializeToElement(ports[0]),
            "array" => JsonSerializer.SerializeToElement(ports),
            _ => JsonSerializer.SerializeToElement($"{ports[0]}-{ports[1]}"),
        };
        Channel<NodeExecutionContext> entered = Channel.CreateUnbounded<NodeExecutionContext>();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkflowFixture fixture = new(SocketNodeTests.Find(type), new ConstantValueNode(), new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await entered.Writer.WriteAsync(context, token).ConfigureAwait(true);
            await release.Task.WaitAsync(token).ConfigureAwait(true);
            return NodeResult.Success(context.Inputs["value"]);
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("parallel-receiver", "parallel receiver",
            [new("ports", "value.constant", JsonSerializer.SerializeToElement(new { type = "auto", text = portValue.GetRawText() })),
             new("receive", type, SocketNodeTests.Settings(new { count = 0 }), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
             WorkflowFixture.Definition("sink")],
            [new("ports", "value", "receive", SettingPorts.Name("localPort")), new("receive", "value", "sink", "value")], Enabled: true);
        CompilationResult compiled = fixture.Compiler.Compile(definition);
        Assert.Empty(compiled.Issues);
        Assert.NotNull(compiled.Workflow);
        await fixture.Sources.ApplyAsync(compiled.Workflow, timeout.Token);
        while (!fixture.Sources.GetSources().Any(source => source.Status.State == ESourceState.Listening))
        {
            await Task.Delay(10, timeout.Token);
        }
        byte[] packet = Convert.FromHexString("2F7800002C000000");
        using TcpClient connection = new();
        using UdpClient datagrams = new();
        if (tcp)
        {
            await connection.ConnectAsync(IPAddress.Loopback, ports[0], timeout.Token);
        }
        try
        {
            NodeExecutionContext? previous = null;
            for (int index = 0; index < 2; index++)
            {
                if (tcp)
                {
                    await TcpFrame.WriteAsync(connection.GetStream(), packet, "lengthPrefix", timeout.Token);
                }
                else
                {
                    string integer = "integer";
                    int portIndex = string.Equals(inputType, integer, StringComparison.Ordinal) ? 0
                                                                                                : index;
                    await datagrams.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, ports[portIndex]), timeout.Token);
                }
                NodeExecutionContext current = await entered.Reader.ReadAsync(timeout.Token);
                Assert.False(release.Task.IsCompleted);
                Assert.NotEqual(previous?.CausationId, current.CausationId);
                previous = current;
            }
            Assert.Contains(fixture.Sources.GetSources(), source => source.Status.State == ESourceState.Listening);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
