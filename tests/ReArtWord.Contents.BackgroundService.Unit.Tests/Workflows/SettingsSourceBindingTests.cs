using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定接続したSourceの実起動・再接続と一件の世代固定を検証します。</summary>
public sealed class SettingsSourceBindingTests
{
    /// <summary>未加工の設定のまま自動起動し、設定接続変更後も取得済み文脈を書き換えません。</summary>
    /// <returns>実Sourceタスクから受け取った設定版です。</returns>
    [Fact]
    public async Task SourceUsesResolvedSettingsAndKeepsCapturedItemDuringReplacement()
    {
        SettingsSourceProbe source = new();
        WorkflowFixture fixture = new(source, new ConstantValueNode());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = new("source-settings", "source settings",
            [new("value", "value.constant", JsonSerializer.SerializeToElement(new { text = "21", type = "integer" })),
             new("source", source.Descriptor.Type, JsonSerializer.SerializeToElement(new { }), StartOnLoad: true)],
            [new("value", "value", "source", SettingPorts.Name("port"))], Enabled: true);
        CompiledWorkflow first = fixture.Compiler.Compile(definition).Workflow!;
        Assert.Empty(first.Incoming["source"]);
        await fixture.Sources.ApplyAsync(first, timeout.Token);
        NodeExecutionContext captured = await source.Contexts.Reader.ReadAsync(timeout.Token);
        Assert.Equal(21, captured.Settings.GetProperty("port").GetInt32());
        WorkflowDefinition changed = definition with { Nodes = [definition.Nodes[0] with { Settings = JsonSerializer.SerializeToElement(new { text = "22", type = "integer" }) }, definition.Nodes[1]] };
        CompiledWorkflow next = fixture.Compiler.Compile(changed).Workflow!;
        await fixture.Sources.ApplyAsync(next, timeout.Token);
        Assert.Equal(first.Revision, captured.Revision);
        Assert.Equal(21, captured.Settings.GetProperty("port").GetInt32());
        await source.Items.Writer.WriteAsync(NodeValue.FromObject(1), timeout.Token);
        while (fixture.Sources.GetSources().Any(item => item.Status.State != ESourceState.Completed))
        {
            await Task.Delay(10, timeout.Token);
        }
        NodeExecutionContext? resumed;

        // 終了状態の通知直後は所有Taskの後処理中の場合があるため、実Hostと同じく反映を継続します。
        while (!source.Contexts.Reader.TryRead(out resumed))
        {
            await fixture.Sources.ReconcileAsync(timeout.Token);
            await Task.Delay(10, timeout.Token);
        }
        Assert.NotNull(resumed);
        Assert.Equal(next.Revision, resumed.Revision);
        Assert.Equal(22, resumed.Settings.GetProperty("port").GetInt32());
        Assert.Equal("{}", first.Definition.Nodes[1].Settings.GetRawText());
        Assert.Equal("{}", next.Definition.Nodes[1].Settings.GetRawText());
    }
}
