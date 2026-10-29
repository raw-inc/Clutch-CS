using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>再接続後も、完了履歴の先頭ページ以外から活動を復元します。</summary>
public sealed class DashboardActivityTests
{
    private static string ActivityOperation => "executions.activity";

    /// <summary>長時間実行・大きい出力・100件を超える完了を、空のキャッシュから取得します。</summary>
    /// <returns>実体変更前後で同じ完了結果を確認できることです。</returns>
    [Fact]
    public async Task RejoiningPeerRestoresCompletionPagesWithoutOutputPayloadsAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.AddSeconds(-1);
        ExecutionResultView[] history = Enumerable.Range(0, 205).Select(index => View("quick", now.AddSeconds(-2), now.AddTicks(-index))).ToArray();
        ExecutionResultView late = View("long-running", now.AddHours(-1), now);
        ExecutionResultView large = new(new(Guid.NewGuid(), "large-output", "revision", EOutcome.Succeeded, now.AddHours(-2), now,
            [new("node", EOutcome.Succeeded, [new("peer", NodeResult.Success(NodeValue.FromObject(new string('x', 30000))))])]));
        history = [.. history, late, large];
        PeerInfo peer = Peer();
        List<ManagementCommand> requests = [];
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            requests.Add(command);
            return Task.FromResult(Reply(command, history));
        });
        for (int join = 0; join < 2; join++)
        {
            await cache.RefreshAsync("cluster", peer, true);
            DashboardPeerSnapshot snapshot = cache.Read("cluster", peer.PeerId)!;
            Assert.Equal(history.Length, snapshot.Executions!.Entries.Count);
            Assert.Contains("long-running", snapshot.Executions.Entries.Select(entry => entry.Summary!.WorkflowId));
            Assert.Contains("large-output", snapshot.Executions.Entries.Select(entry => entry.Summary!.WorkflowId));
            Assert.All(snapshot.Executions.Entries, entry => Assert.Null(entry.Report));
            Assert.Null(snapshot.Executions.Cursor);
            Assert.Equal(3, requests.Count(command => string.Equals(command.Operation, ActivityOperation, StringComparison.Ordinal) && command.TargetInstanceId == peer.InstanceId));
            peer = peer with { InstanceId = Guid.NewGuid() };
            Assert.Null(cache.Read("cluster", peer.PeerId));
        }
    }

    /// <summary>途中ページの障害では取得済み分を残し、全件取得済みとは表示しません。</summary>
    /// <returns>続き位置を保持して次の巡回で取り直せることです。</returns>
    [Fact]
    public async Task FailedLaterPageKeepsPartialActivityAndRecoversOnNextPollAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.AddSeconds(-1);
        ExecutionResultView[] history = Enumerable.Range(0, 120).Select(index => View("workflow", now.AddSeconds(-2), now.AddTicks(-index))).ToArray();
        PeerInfo peer = Peer();
        bool fail = true;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            if (fail && string.Equals(command.Operation, ActivityOperation, StringComparison.Ordinal) && Query(command).Cursor is not null)
            {
                return Task.FromResult<JsonElement?>(null);
            }
            return Task.FromResult(Reply(command, history));
        });
        await cache.RefreshAsync("cluster", peer, true);
        Assert.Equal(100, cache.Read("cluster", peer.PeerId)!.Executions!.Entries.Count);
        Assert.NotNull(cache.Read("cluster", peer.PeerId)!.Executions!.Cursor);
        fail = false;
        await cache.RefreshAsync("cluster", peer, true);
        Assert.Equal(history.Length, cache.Read("cluster", peer.PeerId)!.Executions!.Entries.Count);
        Assert.Null(cache.Read("cluster", peer.PeerId)!.Executions!.Cursor);
    }

    /// <summary>窓外の履歴を辿り続けず、旧Coreでは取得できた通常履歴を表示します。</summary>
    /// <returns>有限取得と旧経路の互換性です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedWindowStopsAtExpiredEntriesAndSupportsOlderPeersAsync(bool legacy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.AddSeconds(-1);
        ExecutionResultView[] history = [View("recent", now.AddSeconds(-2), now),
            .. Enumerable.Range(0, 200).Select(index => View("expired", now.AddMinutes(-3), now.AddMinutes(-2).AddTicks(-index)))];
        PeerInfo peer = Peer();
        int activityReads = 0;
        using DashboardCache cache = new(() => [Cluster(peer)], (command, _) =>
        {
            if (string.Equals(command.Operation, ActivityOperation, StringComparison.Ordinal))
            {
                activityReads++;
                if (legacy)
                {
                    return Task.FromResult<JsonElement?>(null);
                }
            }
            return Task.FromResult(Reply(command, history));
        });
        await cache.RefreshAsync("cluster", peer, true);
        Assert.Equal("recent", Assert.Single(cache.Read("cluster", peer.PeerId)!.Executions!.Entries).Summary!.WorkflowId);
        Assert.Equal(1, activityReads);
        Assert.Equal(legacy, cache.Read("cluster", peer.PeerId)!.Executions!.Cursor is not null);
    }

    /// <summary>照会のカーソルとバイト上限を実契約から復元します。</summary>
    /// <returns>Coreへ渡されたページ指定です。</returns>
    private static ExecutionHistoryQuery Query(ManagementCommand command) => command.Payload!.Json.Deserialize<ExecutionHistoryQuery>(ContractJson.Options)!;

    /// <summary>実際のページ生成処理を使って管理応答を返します。</summary>
    /// <returns>対応操作だけの応答です。</returns>
    private static JsonElement? Reply(ManagementCommand command, ExecutionResultView[] history) => command.Operation switch
    {
        "executions.activity" => JsonSerializer.SerializeToElement(ExecutionHistory.ActivityPage(history, Query(command)), ContractJson.Options),
        "executions.list" => JsonSerializer.SerializeToElement(ExecutionHistory.Page(history, Query(command)), ContractJson.Options),
        _ => null,
    };

    /// <summary>開始と完了が異なる結果を作ります。</summary>
    /// <returns>元の実行IDを持つ完了結果です。</returns>
    private static ExecutionResultView View(string workflow, DateTimeOffset started, DateTimeOffset completed)
        => new(new(Guid.NewGuid(), workflow, "revision", EOutcome.Succeeded, started, completed, []));

    /// <summary>再参加で識別子を変えられるピアです。</summary>
    /// <returns>通常参加中の実体です。</returns>
    private static PeerInfo Peer() => new("peer", Guid.NewGuid(), "host", "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow);

    /// <summary>同じ論理ピアの現在の実体だけを公開します。</summary>
    /// <returns>発見したクラスタです。</returns>
    private static DiscoveredCluster Cluster(PeerInfo peer) => new("cluster", [peer], [peer.PeerId], DateTimeOffset.UtcNow);
}
