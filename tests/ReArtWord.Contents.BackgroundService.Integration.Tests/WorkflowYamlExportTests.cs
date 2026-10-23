using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの書出し・読込みが、保存や実行を伴わないことを確認します。</summary>
public sealed class WorkflowYamlExportTests
{
    /// <summary>未保存の草稿を変換し、同じ管理経路で元の定義へ戻します。</summary>
    /// <returns>実UDPによる往復と、保存一覧に変更がない確認です。</returns>
    [Fact]
    public async Task ExportAndParseDoNotSaveOrActivateAsync()
    {
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "yaml", CorePeer.AvailablePort(), true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await peer.StartAsync(timeout.Token);
        WorkflowDefinition draft = new("unsaved", "未保存の草稿", [new("value", "control.pass", JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromBytes(new byte[] { 0, 255 }) })], [], Enabled: true);
        JsonElement before = await peer.GetAsync<JsonElement>("workflows.list", timeout.Token);
        string yaml = await peer.GetAsync<string>("workflows.export-yaml", draft, timeout.Token);
        WorkflowDefinition restored = await peer.GetAsync<WorkflowDefinition>("workflows.parse", new WorkflowYamlImport(yaml), timeout.Token);
        Assert.Equal(ContractJson.Hash(draft), ContractJson.Hash(restored));
        Assert.Equal(ContractJson.Hash(before), ContractJson.Hash(await peer.GetAsync<JsonElement>("workflows.list", timeout.Token)));
    }
}
