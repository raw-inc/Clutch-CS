using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>旧Coreを指定した場合も、準備要求の方式を相手実体に合わせ、全段階と再起動後の内容を維持します。</summary>
public sealed class ReplicationPreparationCompatibilityTests(ITestOutputHelper output)
{
    /// <summary>別実行物との双方向保存を検証します。未指定時は現在版同士の実プロセス試験です。</summary>
    /// <returns>通常形式との混在・再送・永続化の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredPeerVersionPreservesSaveAndRestartAsync(bool otherSends)
    {
        string? compatibleCore = Environment.GetEnvironmentVariable("CONTENTS_REPLICATION_COMPAT_CORE");
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer current = new(cluster, "current", port, true), other = new(cluster, "other", port, false, executablePath: compatibleCore);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable currentLifetime = current.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable otherLifetime = other.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await current.StartAsync(timeout.Token).ConfigureAwait(true);
        await other.StartAsync(timeout.Token).ConfigureAwait(true);
        CorePeer sender = current, receiver = other;
        if (otherSends)
        {
            sender = other;
            receiver = current;
        }
        await CorePeer.WaitAsync(async token => (await sender.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == 2, timeout.Token).ConfigureAwait(true);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            compatibilityExecutableSupplied = compatibleCore is not null,
            sender = (await sender.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("buildVersion").GetString(),
            receiver = (await receiver.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("buildVersion").GetString(),
        }));
        string? revision = null;
        WorkflowSaveRequest? last = null;
        WorkflowSaveResult? saved = null;
        string text = string.Empty;
        for (int index = 0; index < 2; index++)
        {
            text = "version:" + index + ":" + new string('x', 131072);
            WorkflowDefinition definition = new("compat-preparation", "Preparation compatibility", [new("value", "control.pass", JsonSerializer.SerializeToElement(new { }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(text) })], []);
            last = new(Guid.NewGuid(), definition, revision);
            saved = await sender.PostAsync<WorkflowSaveResult>("workflows.save", last, timeout.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
            Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
            if (index != 0)
            {
                Assert.Equal(EOutcome.Succeeded, saved.BeforeSnapshot?.Outcome);
            }
            revision = saved.Revision;
        }
        WorkflowSaveResult repeated = await sender.PostAsync<WorkflowSaveResult>("workflows.save", last!, timeout.Token).ConfigureAwait(true);
        Assert.Equal(saved!.Revision, repeated.Revision);
        Assert.Equal(EOutcome.Succeeded, repeated.Shared.Outcome);
        await receiver.StopAsync(timeout.Token).ConfigureAwait(true);
        await receiver.StartAsync(timeout.Token).ConfigureAwait(true);
        JsonElement restored = await receiver.GetAsync<JsonElement>("workflows.read", last!.Definition.Id, timeout.Token).ConfigureAwait(true);
        Assert.Equal(text, restored.GetProperty("saved").GetProperty("definition").GetProperty("nodes")[0].GetProperty("inputs").GetProperty("value").GetProperty("json").GetString());
        Assert.Equal(saved.Revision, restored.GetProperty("saved").GetProperty("revision").GetString());
    }
}
