using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>予定時刻に不在だったPCへの処理を、復帰時の方針に従って扱うことを確認します。</summary>
public sealed class ScheduleDeferredTargetTests
{
    /// <summary>使えるPCの処理は先に完了し、不在PCには遅延方針で許可された場合だけ配送します。</summary>
    /// <returns>二台の実Coreと、実ファイルへの追記による確認。</returns>
    [Theory]
    [InlineData("delay", 1000, true, null)]
    [InlineData("skip", 1000, false, null)]
    [InlineData("within", 60000, true, null)]
    [InlineData("within", 50, false, null)]
    [InlineData("delay", 1000, true, EPcExecutionMode.One)]
    [InlineData("delay", 1000, true, EPcExecutionMode.All)]
    public async Task ReturningTargetUsesTheCapturedMissedPolicyAsync(string policy, int tolerance, bool expected, EPcExecutionMode? pcMode)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", 900);
        CorePeer second = new(cluster, "b", port, false, "host-b", 100);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string localFile = await CreateOutputAsync(first).ConfigureAwait(true);
        string remoteFile = await CreateOutputAsync(second).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        await ReadyAsync(second).ConfigureAwait(true);
        await second.StopAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Any(peer => string.Equals(peer.GetProperty("peerId").GetString(), "b", StringComparison.Ordinal)
                && string.Equals(peer.GetProperty("status").GetString(), "unavailable", StringComparison.Ordinal))).ConfigureAwait(true);
        WorkflowDefinition definition = new("deferred", "Deferred scheduled target", [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at = DateTimeOffset.UtcNow.AddSeconds(1), missed = policy, toleranceMilliseconds = tolerance }), StartOnLoad: true),
            Append("local", localFile, "a"), Append("remote", remoteFile, "b"),
        ], [new("timer", "value", "local", "trigger"), new("timer", "value", "remote", "trigger")], Enabled: true);
        if (pcMode is not null)
        {
            string[] hostIds = ["host-b"];
            string remote = "remote";
            NodeDefinition selection = new("pc", PcSelectionPorts.SelectNode, JsonSerializer.SerializeToElement(new { mode = "selected", hostIds }));
            definition = definition with
            {
                Nodes = [.. definition.Nodes.Select(node =>
                {
                    if (string.Equals(node.Id, remote, StringComparison.Ordinal))
                    {
                        return node with { Target = null, PcExecution = pcMode.Value };
                    }
                    return node;
                }), selection],
                Wires = [.. definition.Wires, new("pc", "value", "remote", PcSelectionPorts.Execution)],
            };
        }
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sources").EnumerateArray()
            .Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "timer", StringComparison.Ordinal)
                && string.Equals(source.GetProperty("status").GetProperty("state").GetString(), "completed", StringComparison.Ordinal))).ConfigureAwait(true);
        if (expected)
        {
            await CorePeer.WaitAsync(async token => string.Equals(await FileObservation.ReadTextAsync(localFile, token).ConfigureAwait(true), "tick\n", StringComparison.Ordinal)).ConfigureAwait(true);
        }
        Assert.Equal(string.Empty, await FileObservation.ReadTextAsync(remoteFile).ConfigureAwait(true));
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(second).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement queue = (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sourceQueue");
            return queue.GetProperty("running").GetInt32() == 0 && queue.GetProperty("waiting").GetInt32() == 0;
        }).ConfigureAwait(true);
        string content = string.Empty;
        if (expected)
        {
            content = "tick\n";
        }
        Assert.Equal(content, await FileObservation.ReadTextAsync(remoteFile).ConfigureAwait(true));
    }

    /// <summary>各PCの許可範囲に、追記先の空ファイルを用意します。</summary>
    /// <returns>そのPCの追記先。</returns>
    private static async Task<string> CreateOutputAsync(CorePeer peer)
    {
        string root = Path.Combine(peer.DirectoryPath, "content");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "scheduled.txt");
        await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(true);
        return path;
    }

    /// <summary>実行先に対応する許可パスへ一行を書き込みます。</summary>
    /// <returns>追記回数を確認できる処理。</returns>
    private static NodeDefinition Append(string id, string path, string peer) => new(id, "file.append", JsonSerializer.SerializeToElement(new { path }),
        new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("tick\n") }, Target: new(EExecutionScope.One, [peer]));

    /// <summary>同期と共通設定の準備が済んだ状態を待ちます。</summary>
    /// <returns>通常参加の確認。</returns>
    private static Task ReadyAsync(CorePeer peer) => CorePeer.WaitAsync(async token =>
        string.Equals((await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("status").GetString(), "ready", StringComparison.Ordinal)
        && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null);
}
