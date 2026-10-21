using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>信号と共有保存を継続した7DBをランダム停止・再起動し、同期と一回実行を検証します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class RandomizedRestartTests(ITestOutputHelper output)
{
    /// <summary>再現可能な二つの順序で、部分停止・全停止・旧要求照会を実行します。</summary>
    /// <returns>全DBの収束、実信号、重複なしと不明結果の区別です。</returns>
    // 抑制理由: 認証等には使用せず、同じseedで障害注入順序を再現するための試験用乱数です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "隔離試験の停止順・待ち時間を同じseedで再現するため。秘密値には使いません。")]
    [Theory]
    [InlineData(20261015)]
    [InlineData(20261016)]
    public async Task SevenDatabasesConvergeAfterRandomRestartsDuringWorkAsync(int seed)
    {
        RestartCluster cluster = new(seed);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clusterLifetime = cluster.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));
        int inputPort = CorePeer.AvailablePort();
        using UdpClient receiver = new(new IPEndPoint(IPAddress.Loopback, 0));
        int outputPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        UdpObserver observer = new(new(Guid.NewGuid().ToString("N"), new(cluster.Peers[0].InterfaceId, "127.0.0.1", "127.255.255.255", cluster.Port)));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
        RestartWorkload workload = new(observer, cluster.Id, cluster.Online, inputPort, receiver, seed ^ 9173);
        using CancellationTokenSource producers = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using CancellationTokenSource capture = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task sending = Task.CompletedTask, writing = Task.CompletedTask, receiving = Task.CompletedTask;
        bool completed = false;
        IReadOnlyList<object> databases = [], recovered = [];
        string report = Path.Combine(cluster.Peers[0].DirectoryPath, "random-restarts.json");
        output.WriteLine(report);
        try
        {
            await RestartFixtureData.SeedAsync(cluster.Peers, 11008, timeout.Token);
            await cluster.StartAsync(0, timeout.Token);
            await RestartFixtureData.InstallAsync(cluster.Peers[0], inputPort, outputPort, timeout.Token);
            for (int index = 1; index < 7; index++)
            {
                await cluster.StartAsync(index, timeout.Token);
            }
            await observer.StartAsync(timeout.Token);
            await cluster.WaitReadyAsync(timeout.Token);
            cluster.Capture("initial");
            receiving = workload.ReceiveAsync(capture.Token);
            sending = workload.SendAsync(producers.Token);
            writing = workload.WriteAsync(producers.Token);
            await workload.ProbeAsync(timeout.Token);
            await CorePeer.WaitAsync(_ => Task.FromResult(6 <= workload.Attempts.Count
                && workload.Received.Keys.Any(key => key.StartsWith("manual-", StringComparison.Ordinal))), TimeSpan.FromSeconds(40), timeout.Token);

            // 最初は最低5台を生かし、停止と復帰が重なる順序で全Peerを一度ずつ落とします。
            Random random = new(seed);
            int[] rolling = Enumerable.Range(0, 7).ToArray();
            random.Shuffle(rolling);
            Queue<int> offline = new();
            foreach (int index in rolling)
            {
                await Task.Delay(random.Next(40, 350), timeout.Token);
                await cluster.StopAsync(index, workload.WritingPeer, timeout.Token);
                offline.Enqueue(index);
                if (offline.Count == 2)
                {
                    await Task.Delay(random.Next(80, 400), timeout.Token);
                    await cluster.StartAsync(offline.Dequeue(), timeout.Token);
                }
            }
            await cluster.StartAsync(offline.Dequeue(), timeout.Token);
            await Task.Delay(6000, timeout.Token);
            cluster.Capture("after-rolling");

            // 全停止の前後でも入力側を止めず、書込み応答待ち・受信不能の区間を記録します。
            int[] stopped = Enumerable.Range(0, 7).ToArray(), restarted = Enumerable.Range(0, 7).ToArray();
            random.Shuffle(stopped);
            random.Shuffle(restarted);
            foreach (int index in stopped)
            {
                await Task.Delay(random.Next(80, 400), timeout.Token);
                await cluster.StopAsync(index, workload.WritingPeer, timeout.Token);
            }
            await Task.Delay(750, timeout.Token);
            foreach (int index in restarted)
            {
                await Task.Delay(random.Next(80, 450), timeout.Token);
                await cluster.StartAsync(index, timeout.Token);
            }
            await Task.Delay(8000, timeout.Token);
            await producers.CancelAsync();
            await Task.WhenAll(sending, writing);
            await cluster.WaitReadyAsync(timeout.Token);
            cluster.Capture("recovered");
            await workload.ProbeAsync(timeout.Token);
            recovered = await workload.VerifyRequestsAsync(timeout.Token);
            await Task.Delay(1500, timeout.Token);
            await cluster.WaitReadyAsync(timeout.Token);
            databases = await cluster.VerifyDatabasesAsync(workload.Attempts, timeout.Token);
            Assert.All(workload.Received.Values, count => Assert.Equal(1, count));
            Assert.True(5 <= workload.Received.Keys.Count(key => key.StartsWith("source-", StringComparison.Ordinal)));
            Assert.Contains(workload.Received.Keys, key => key.StartsWith("manual-", StringComparison.Ordinal));
            Assert.Contains(workload.Attempts, attempt => attempt.Reply.Outcome == EOutcome.Succeeded);
            completed = true;
        }
        finally
        {
            await producers.CancelAsync();
            await capture.CancelAsync();
            try
            {
                await Task.WhenAll(sending, writing, receiving);
            }
            finally
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    seed, completed, peerCount = 7, historyRowsPerDatabase = 11008, workflows = 38, nodesPerLargeWorkflow = 200,
                    sourceIntervalMilliseconds = 333, workload.Sent, received = workload.Received,
                    attempts = workload.Attempts, recovered, databases, events = cluster.Events, observations = cluster.Observations,
                }, ContractJson.Options));
                output.WriteLine($"seed={seed}; completed={completed}; sent={workload.Sent}; received={workload.Received.Count}; management={workload.Attempts.Count}");
            }
        }
    }
}
