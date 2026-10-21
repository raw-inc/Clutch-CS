using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Testing.Performance;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>外部環境を参照せず、性能試験の3 Coreと計測対象PIDを同じ寿命で所有します。</summary>
internal sealed class PerformanceCluster : IAsyncDisposable
{
    private readonly List<int> processes = [];

    /// <summary>ランダムなクラスタIDと専用のループバック・保存先を割り当てます。</summary>
    // 抑制理由: Peersへ移した全Coreを、この所有者のDisposeAsyncで破棄します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Peers配列がCoreの所有権を持ち、DisposeAsyncが全要素を破棄します。")]
    internal PerformanceCluster()
    {
        string id = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        Peers = [new(id, "a", port, true), new(id, "b", port, false), new(id, "c", port, false)];
    }

    /// <summary>このfixtureだけが生成したPeerです。</summary>
    internal CorePeer[] Peers
    {
        get;
    }

    /// <summary>全Coreの実参加を確認し、起動の費用と通常実行の費用を分けます。</summary>
    /// <returns>3 PeerのReady確認です。</returns>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (CorePeer peer in Peers)
        {
            Directory.CreateDirectory(Path.Combine(peer.DirectoryPath, "content"));
            await peer.StartAsync(cancellationToken).ConfigureAwait(false);
            JsonElement health = await peer.GetAsync<JsonElement>("health", cancellationToken).ConfigureAwait(false);
            processes.Add(health.GetProperty("processId").GetInt32());
        }
        await CorePeer.WaitAsync(async token => (await Peers[0].GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("peers").EnumerateArray()
            .Count(peer => peer.GetProperty("status").GetString() is "ready") == Peers.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存済み定義の版を固定し、開始を一回だけ要求して同じ操作を照会します。</summary>
    /// <returns>結果不明を成功へ読み替えない実行結果です。</returns>
    internal async Task<WorkflowRunStatus> RunAsync(WorkflowDefinition definition, WorkflowSaveResult saved, TimeSpan? completionTimeout = null, CancellationToken cancellationToken = default)
    {
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus result = await Peers[0].PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision), cancellationToken).ConfigureAwait(false);
        try
        {
            await CorePeer.WaitAsync(async token =>
            {
                result = await Peers[0].GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(false);
                return !result.Running;
            }, completionTimeout ?? TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Assert.Fail(JsonSerializer.Serialize(new { Reason = "Workflow completion deadline elapsed.", LastStatus = result }));
        }
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        return result;
    }

    /// <summary>子Coreの現在値を取得し、試験プロセスやHTTP fixtureを含めません。</summary>
    /// <returns>同じPID集合のリソース観測です。</returns>
    internal ProcessResourceSample[] Capture() => processes.Select(ProcessResourceSample.Capture).ToArray();

    /// <summary>試験中の例外でも所有しているCoreだけを終了します。</summary>
    /// <returns>全所有プロセスの終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(Peers.Select(static peer => ((IAsyncDisposable)peer).DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
