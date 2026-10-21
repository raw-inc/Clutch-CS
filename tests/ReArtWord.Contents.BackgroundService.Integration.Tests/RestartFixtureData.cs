using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ランダム再起動用の履歴とWFを、試験所有の保存先へだけ構築します。</summary>
internal static class RestartFixtureData
{
    /// <summary>停止中の新規DBだけに同じ基準履歴を複製し、7DBを独立した実ファイルにします。</summary>
    /// <returns>起動前の合成履歴準備です。</returns>
    internal static async Task SeedAsync(IReadOnlyList<CorePeer> peers, int count, CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(peers[0].DirectoryPath, "state");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "state.db");
        SqliteRepository repository = new(source);
        DateTimeOffset now = DateTimeOffset.UtcNow.AddDays(-1);
        for (int offset = 0; offset < count; offset += 64)
        {
            List<DurableMutation> batch = [];
            for (int index = offset; index < Math.Min(count, offset + 64); index++)
            {
                string key = Guid.CreateVersion7(now).ToString("N");
                batch.Add(new("receipts:restart-fixture", key, null, NodeValue.FromObject(new
                {
                    Fingerprint = key, InstanceId = Guid.NewGuid(), ExpiresAt = now,
                    Response = new { Outcome = EOutcome.Succeeded, Value = NodeValue.FromObject(new string('h', 512)) },
                })));
            }
            Assert.Equal(EOutcome.Succeeded, (await repository.ApplyBatchAsync(Guid.CreateVersion7(now), batch, cancellationToken).ConfigureAwait(false)).Outcome);
        }
        foreach (CorePeer peer in peers.Skip(1))
        {
            string destination = Path.Combine(peer.DirectoryPath, "state");
            Directory.CreateDirectory(destination);
            File.Copy(source, Path.Combine(destination, "state.db"), false);
        }
    }

    /// <summary>本番のWF数と大きい定義を近づけ、外部APIや実ファイル副作用は使いません。</summary>
    /// <returns>初期保存の確認です。</returns>
    internal static async Task InstallAsync(CorePeer peer, int inputPort, int outputPort, CancellationToken cancellationToken = default)
    {
        TargetDefinition sameReceiver = new(EExecutionScope.One, SourcePeer: true);
        string[] hosts = Enumerable.Range(0, 7).Select(index => "host-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        WorkflowDefinition source = new("chaos-source", "OSC relay during restart", [
            new("pc", "execution.pc.select", JsonSerializer.SerializeToElement(new { mode = "selected", hostIds = hosts })),
            new("receive", "osc.udp.receive", JsonSerializer.SerializeToElement(new { interfaceId = peer.InterfaceId, localAddress = "127.0.0.1", localPort = inputPort }), StartOnLoad: true),
            new("send", "osc.udp.send", SendSettings(peer.InterfaceId, outputPort), Target: sameReceiver),
        ], [new("pc", "value", "receive", "$execution.pc"), new("receive", "value", "send", "value")], Enabled: true);
        WorkflowDefinition manual = new("chaos-manual", "One manual side effect", [
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 250 }), Target: new(EExecutionScope.One, ["p0"])),
            new("send", "udp.send", SendSettings(peer.InterfaceId, outputPort), Target: new(EExecutionScope.One, ["p0"])),
        ], [new("delay", "value", "send", "value")]);
        foreach (WorkflowDefinition definition in new[] { source, manual, Editable(0) }.Concat(Enumerable.Range(0, 35).Select(index => Editable(0) with
        {
            Id = "background-" + index.ToString(CultureInfo.InvariantCulture), Name = "Inactive fixture " + index.ToString(CultureInfo.InvariantCulture),
        })))
        {
            WorkflowSaveResult saved = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), cancellationToken).ConfigureAwait(false);
            Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        }
    }

    /// <summary>200ノードの設定版を一か所だけ変更します。</summary>
    /// <returns>保存競合・準備回復の対象です。</returns>
    internal static WorkflowDefinition Editable(int generation) => new("chaos-edit", "Configuration under restart", Enumerable.Range(0, 200).Select(index =>
        new NodeDefinition("value-" + index.ToString(CultureInfo.InvariantCulture), "value.constant", JsonSerializer.SerializeToElement(new { text = index.ToString(CultureInfo.InvariantCulture) }),
            Position: new(index * 10 + generation, index * 5))).ToArray(), []);

    /// <summary>通信先を試験のループバック待受だけへ固定します。</summary>
    /// <returns>通常のUDPノード設定です。</returns>
    private static JsonElement SendSettings(string nic, int port) => JsonSerializer.SerializeToElement(new
    {
        interfaceId = nic, localAddress = "127.0.0.1", remoteAddress = "127.0.0.1", remotePort = port,
    });
}
