using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>別Coreの音声生成・予約・実媒体検証を、模擬APIと実通信で反復します。</summary>
[Collection("Performance"), Trait("Category", "Performance")]
public sealed class AudioWorkloadProcessTests(ITestOutputHelper output)
{
    /// <summary>7並列のWFを2本ずつ反復し、失敗・期限後も同じ保存先で再開できます。</summary>
    /// <returns>実出力、要求数、資源推移、予約と一時ファイルの解放確認です。</returns>
    [Fact]
    public async Task ParallelSpeechReleasesReservationsAfterSuccessFailureAndTimeoutAsync()
    {
        PerformanceCluster cluster = new();
        LoopbackSpeechServer server = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cores = cluster.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable http = server.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(6));
        string probe = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFPROBE") ?? "C:/opt/ffmpeg/current/bin/ffprobe.exe";
        Assert.True(Path.IsPathFullyQualified(probe) && File.Exists(probe), "Set CONTENTS_TEST_FFPROBE to an installed ffprobe executable.");
        foreach (CorePeer peer in cluster.Peers)
        {
            await File.AppendAllTextAsync(Path.Combine(peer.DirectoryPath, "contents.toml"), "\n[media]\nffprobePath = " + JsonSerializer.Serialize(probe.Replace('\\', '/')) + "\n", deadline.Token).ConfigureAwait(true);
        }
        await cluster.StartAsync(deadline.Token).ConfigureAwait(true);
        List<ProcessResourceSample[]> samples = [cluster.Capture()];
        List<object> rounds = [];
        List<(string Path, string Text)> expected = [];
        string report = Path.Combine(cluster.Peers[0].DirectoryPath, "audio-workload.json");
        try
        {
            for (int round = 0; round < 3; round++)
            {
                List<(WorkflowDefinition Definition, WorkflowSaveResult Saved)> workflows = [];
                for (int shot = 0; shot < 2; shot++)
                {
                    string id = FormattableString.Invariant($"audio-{round}-{shot}");
                    AudioItem[] items = Enumerable.Range(0, 12).Select(index =>
                    {
                        string text = id + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string path = Path.Combine(cluster.Peers[1].DirectoryPath, "content", id, text + ".wav");
                        expected.Add((path, text));
                        return new AudioItem(text, path);
                    }).ToArray();
                    WorkflowDefinition definition = Definition(id, items, cluster.Peers[1].InterfaceId, server.Url);
                    WorkflowSaveResult saved = await SaveAsync(cluster.Peers[0], definition, deadline.Token).ConfigureAwait(true);
                    workflows.Add((definition, saved));
                }
                Stopwatch watch = Stopwatch.StartNew();
                Task<WorkflowRunStatus[]> pending = Task.WhenAll(workflows.Select(item => cluster.RunAsync(item.Definition, item.Saved, cancellationToken: deadline.Token)));
                try
                {
                    while (!pending.IsCompleted)
                    {
                        samples.Add(cluster.Capture());
                        await Task.Delay(250, deadline.Token).ConfigureAwait(true);
                    }
                }
                finally
                {
                    await pending.ConfigureAwait(true);
                }
                rounds.Add(new { round, milliseconds = watch.Elapsed.TotalMilliseconds, files = 24 });
                await Task.Delay(2000, deadline.Token).ConfigureAwait(true);
                samples.Add(cluster.Capture());
            }
            foreach ((string path, string text) in expected)
            {
                Assert.Equal(LoopbackSpeechServer.Wave(text), await File.ReadAllBytesAsync(path, deadline.Token).ConfigureAwait(true));
                Assert.Equal(1, server.Requests[text]);
            }
            foreach (string failure in new[] { "error", "timeout" })
            {
                string path = Path.Combine(cluster.Peers[1].DirectoryPath, "content", "recovery", failure + ".wav");
                int timeout = failure switch { "timeout" => 250, _ => 10000 };
                WorkflowDefinition definition = Definition(failure, [new(failure, path)], cluster.Peers[1].InterfaceId, server.Url, timeout);
                WorkflowSaveResult saved = await SaveAsync(cluster.Peers[0], definition, deadline.Token).ConfigureAwait(true);
                Guid operation = Guid.NewGuid();
                WorkflowRunStatus failed = await cluster.Peers[0].PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision), deadline.Token).ConfigureAwait(true);
                await CorePeer.WaitAsync(async token =>
                {
                    failed = await cluster.Peers[0].GetAsync<WorkflowRunStatus>("runs.read", operation, token).ConfigureAwait(true);
                    return !failed.Running;
                }, deadline.Token).ConfigureAwait(true);
                Assert.NotEqual(EOutcome.Succeeded, failed.Outcome);
                Assert.False(File.Exists(path));
                Assert.Equal(1, server.Requests[failure]);
                string text = failure + "-recovered";
                WorkflowDefinition recovery = Definition(text, [new(text, path)], cluster.Peers[1].InterfaceId, server.Url);
                WorkflowSaveResult recoverySaved = await SaveAsync(cluster.Peers[0], recovery, deadline.Token).ConfigureAwait(true);
                await cluster.RunAsync(recovery, recoverySaved, cancellationToken: deadline.Token).ConfigureAwait(true);
                Assert.Equal(LoopbackSpeechServer.Wave(text), await File.ReadAllBytesAsync(path, deadline.Token).ConfigureAwait(true));
                rounds.Add(new { failure, outcome = failed.Outcome, recovered = true });
            }
            await Task.Delay(5000, deadline.Token).ConfigureAwait(true);
            samples.Add(cluster.Capture());
            Assert.Equal(76, server.Requests.Values.Sum());
            Assert.All(server.Requests.Values, count => Assert.Equal(1, count));
            foreach (CorePeer peer in cluster.Peers)
            {
                await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("runningNodes").GetArrayLength() == 0, deadline.Token).ConfigureAwait(true);
                Assert.Equal(0, ActiveReservations(Path.Combine(peer.DirectoryPath, "state", "state.db")));
            }
            Assert.Equal(74, Directory.EnumerateFiles(Path.Combine(cluster.Peers[1].DirectoryPath, "content"), "*", SearchOption.AllDirectories).Count());
            Assert.All(samples.SelectMany(sample => sample), sample => Assert.InRange(sample.PrivateBytes, 0, 1024L * 1024 * 1024));
        }
        finally
        {
            string json = JsonSerializer.Serialize(new { parallelismPerWorkflow = 7, shots = 2, rounds, generationRequests = server.Requests, samples,
                executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_CORE"), processors = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT") });
            await File.WriteAllTextAsync(report, json, CancellationToken.None).ConfigureAwait(true);
            output.WriteLine(report);
        }
    }

    /// <summary>反復の値を遠隔音声ノードに渡し、終了時まで出力の所有を維持します。</summary>
    /// <returns>外部URL・本番保存先を含まないWFです。</returns>
    private static WorkflowDefinition Definition(string id, AudioItem[] items, string nic, string url, int timeout = 10000)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        return new(id, id, [
            new("loop", "control.parallelForEach", JsonSerializer.SerializeToElement(new { maxConcurrency = 7 }),
                new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(items) }, new(EExecutionScope.One, ["a"])),
            new("speech", "ai.audio.qwen.generate", JsonSerializer.SerializeToElement(new
            {
                endpoint = url + "generate", model = "qwen3-tts-vc-2026-01-22", voice = "fixture-voice",
                interfaceId = nic, localAddress = "127.0.0.1", timeoutMs = timeout,
            }), Target: new(EExecutionScope.One, ["b"])),
            new("end", "control.forEach.end", empty, Target: new(EExecutionScope.One, ["a"]))],
            [new("loop", "item", "speech", "value", Path: "/Text"), new("loop", "item", "speech", "$settings.outputPath", Path: "/Path"),
                new("loop", "loop", "end", "loop"), new("speech", "value", "end", "value")]);
    }

    /// <summary>隔離クラスタだけに保存し、活性化失敗を実行試験へ持ち越しません。</summary>
    /// <returns>版を固定して実行するための保存結果です。</returns>
    private static async Task<WorkflowSaveResult> SaveAsync(CorePeer peer, WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        WorkflowSaveResult saved = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), cancellationToken).ConfigureAwait(false);
        Assert.True(saved.Activation.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(saved));
        return saved;
    }

    /// <summary>全Coreの稼働中に別接続でDBを読み、完了した操作の予約が残らないことを確認します。</summary>
    /// <returns>解放されていない予約の数です。</returns>
    private static long ActiveReservations(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM records WHERE kind='entry' AND scope='file:reservations:slots' AND json_type(payload,'$.Value') NOT IN ('null')";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>各合成の入力と、他要求から独立した出力先です。</summary>
    private sealed record AudioItem(string Text, string Path);
}
