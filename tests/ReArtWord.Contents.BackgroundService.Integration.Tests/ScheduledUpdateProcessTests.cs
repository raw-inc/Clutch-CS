using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Scheduling;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>専用一時配置の二Coreで、予約から更新・Hookまで実通信を確認します。</summary>
public sealed class ScheduledUpdateProcessTests
{
    /// <summary>指定日時で一つの実Peerを更新し、通常Hookワークフローが元IDを受け取ります。</summary>
    /// <returns>GUI管理経路・永続予約・独立Updater・後続処理の確認です。</returns>
    [Fact]
    public async Task ReservationUpdatesOwnedPeerAndTriggersWorkflowHookAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-scheduled-update", Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "package");
        ReleaseManifest old = await RollingUpdatePeer.PackageAsync(package).ConfigureAwait(true);
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        RollingUpdatePeer owner = new(root, cluster, "a", port, package, old, true);
        RollingUpdatePeer target = new(root, cluster, "b", port, package, old, false);
        ClientHostProcess client = new(Path.Combine(root, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = target.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = ((IAsyncDisposable)client).ConfigureAwait(true);
        TimeSpan testTimeout = TimeSpan.FromMinutes(3);
        using CancellationTokenSource timeout = new(testTimeout);
        await owner.StartAsync(timeout.Token).ConfigureAwait(true);
        Guid previous = await target.StartAsync(timeout.Token).ConfigureAwait(true);
        await client.StartAsync(timeout.Token).ConfigureAwait(true);
        using (HttpResponseMessage configured = await client.SendAsync(HttpMethod.Put, "client/networks", new[] { owner.Binding }, cancellationToken: timeout.Token).ConfigureAwait(true))
        {
            configured.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage status = await client.SendAsync(HttpMethod.Get, "api/status", clusterId: cluster, peerId: "a", cancellationToken: token).ConfigureAwait(true);
            if (!status.IsSuccessStatusCode)
            {
                return false;
            }
            JsonElement value = await status.Content.ReadFromJsonAsync<JsonElement>(ContractJson.Options, token).ConfigureAwait(true);
            return value.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
        }, timeout.Token).ConfigureAwait(true);
        WorkflowDefinition hook = new("update-hook", "Update completion hook", [
            new("hook", "control.hook", JsonSerializer.SerializeToElement(new { name = "update.completed", peer = "a" }), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            new("log", "control.log", JsonSerializer.SerializeToElement(new { level = "WARN", message = "Update completed" }), Target: new(EExecutionScope.One, ["a"])),
        ], [new("hook", "value", "log", "value")], Enabled: true);
        WorkflowSaveResult saved = await PostAsync<WorkflowSaveResult>(client, cluster, "api/workflows/save", new WorkflowSaveRequest(Guid.NewGuid(), hook, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        string payload = Path.Combine(root, cluster, "a", "staging", "selected-payload");
        foreach (ReleaseFile file in old.Files)
        {
            string destination = Path.Combine(payload, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(package, file.Path), destination);
        }
        ClusterUpdateRequest update = new(Guid.NewGuid(), old with { ReleaseId = "new" }, payload, ["b"], EUpdateFailurePolicy.KeepCompatibleMixed, PeerTimeoutMilliseconds: 60000);

        // 編集前の予約が先に発火しないよう、初回の時刻は試験全体の期限より後に置きます。
        UpdateScheduleRequest reservation = new(Guid.NewGuid(), new(DateTimeOffset.UtcNow.Add(testTimeout), 120, update));
        UpdateScheduleResult accepted = await PostAsync<UpdateScheduleResult>(client, cluster, "api/updates/schedules/save", reservation, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
        Assert.Equal(previous, accepted.View!.Schedule.PeerInstances["b"]);
        UpdateScheduleRequest changed = reservation with { OperationId = Guid.NewGuid(), ExpectedVersion = accepted.View.Version, Plan = reservation.Plan with { StartAt = DateTimeOffset.UtcNow.AddSeconds(4) } };
        UpdateScheduleResult modified = await PostAsync<UpdateScheduleResult>(client, cluster, "api/updates/schedules/save", changed, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, modified.Outcome);
        UpdateScheduleView? finished = null;
        await CorePeer.WaitAsync(async token =>
        {
            finished = await GetAsync<UpdateScheduleView>(client, cluster, "api/updates/schedules/" + update.OperationId, token).ConfigureAwait(true);
            return finished.Schedule.State is EUpdateScheduleState.Completed or EUpdateScheduleState.Expired;
        }, TimeSpan.FromSeconds(120), timeout.Token).ConfigureAwait(true);
        UpdatePeerResult? local = await target.ReadUpdateResultAsync(update.OperationId).ConfigureAwait(true);
        Assert.True(finished!.Schedule.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(new
        {
            finished.Schedule.State, finished.Schedule.Outcome, finished.Schedule.Reason, Peers = finished.Schedule.Update?.Peers, Local = local,
        }));
        Assert.NotEqual(previous, (await target.HealthAsync(timeout.Token).ConfigureAwait(true)).GetProperty("instanceId").GetGuid());
        NodeResult? observed = null;
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await GetAsync<ExecutionHistoryPage>(client, cluster, "api/executions", token).ConfigureAwait(true);
            observed = history.Entries.Where(entry => string.Equals(entry.Report?.WorkflowId, hook.Id, StringComparison.Ordinal)).SelectMany(entry => entry.Report!.Nodes)
                .Where(node => string.Equals(node.NodeId, "hook", StringComparison.Ordinal)).SelectMany(node => node.Targets).Select(item => item.Result)
                .FirstOrDefault(result => result.Outcome == EOutcome.Succeeded && result.Outputs.ContainsKey("value"));
            return observed is not null;
        }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(update.OperationId, observed!.Outputs["value"].Json.GetProperty("operationId").GetGuid());
        Assert.Equal("succeeded", observed.Outputs["value"].Json.GetProperty("outcome").GetString());
    }

    /// <summary>画面と同じHTTP/UDP経路を使用します。</summary>
    /// <returns>元Peerの型付き応答です。</returns>
    private static async Task<T> PostAsync<T>(ClientHostProcess client, string cluster, string path, object request, CancellationToken cancellationToken = default)
    {
        Guid operation = JsonSerializer.SerializeToElement(request, ContractJson.Options).GetProperty("operationId").GetGuid();
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, path, request, cluster, "a", operation, cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
    }

    /// <summary>同じ起点Peerへ結果を照会します。</summary>
    /// <returns>更新を再実行しない読取りです。</returns>
    private static async Task<T> GetAsync<T>(ClientHostProcess client, string cluster, string path, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, path, clusterId: cluster, peerId: "a", cancellationToken: cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(true))!;
    }
}
