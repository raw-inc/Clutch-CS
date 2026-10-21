using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>進捗を送った実体だけを追跡し、反復とWFの終了で同じ表示資源を回収します。</summary>
internal sealed class WorkflowProgressLifetime(ProgressSession session, Func<ProgressSession, IReadOnlyList<PeerInfo>, CancellationToken, Task>? complete) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Dictionary<Guid, PeerInfo>> scopes = [];
    private readonly Dictionary<Guid, PeerInfo> targets = [];
    private readonly List<Task> pending = [];
    private bool closed;

    /// <summary>実配送前に所有範囲と対象実体を固定します。</summary>
    /// <returns>このProgressへ渡す実行識別です。</returns>
    internal ProgressSession Register(Guid scope, PeerInfo target)
    {
        using (gate.EnterScope())
        {
            if (closed)
            {
                throw new InvalidOperationException("終了したWFへ進捗を追加できません。");
            }
            targets[target.InstanceId] = target;
            if (scope != Guid.Empty)
            {
                if (!scopes.TryGetValue(scope, out Dictionary<Guid, PeerInfo>? peers))
                {
                    peers = [];
                    scopes.Add(scope, peers);
                }
                peers[target.InstanceId] = target;
            }
            if (scope == Guid.Empty)
            {
                return session;
            }
            return session with { ScopeId = scope };
        }
    }

    /// <summary>子発火を含め終了した反復だけを回収し、WF全体の進捗は維持します。</summary>
    // 抑制理由: 通知Taskをpendingで所有し、WF終了時に必ずawaitします。通知待ちは128件までです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "終了通知はpendingで保持し、DisposeAsyncで観測します。")]
    internal void CompleteScope(Guid id)
    {
        using (gate.EnterScope())
        {
            if (closed || !scopes.Remove(id, out Dictionary<Guid, PeerInfo>? peers))
            {
                return;
            }
            pending.RemoveAll(task => task.IsCompleted);
            if (pending.Count < 128)
            {
                pending.Add(CompleteAsync(session with { ScopeId = id }, peers.Values.ToArray()));
            }

            // 通知待ちの上限を超えた範囲も、最後のWF全体の終了通知で解放します。
        }
    }

    /// <summary>表示の回収失敗で実行結果を変えず、必ず有限時間で観測を終えます。</summary>
    /// <returns>表示先への終了通知試行です。</returns>
    private async Task CompleteAsync(ProgressSession completed, PeerInfo[] peers, CancellationToken cancellationToken = default)
    {
        if (complete is null || peers.Length == 0)
        {
            return;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await complete(completed, peers, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // 切断先の表示は受信側の有限保持で回収し、完了したWFを再実行しません。
        }
    }

    /// <summary>正常・異常・取消の終了で、子の通知と全対象の表示を回収します。</summary>
    /// <returns>所有する全終了通知の確認です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Task[] notifications;
        PeerInfo[] peers;
        using (gate.EnterScope())
        {
            closed = true;
            notifications = pending.ToArray();
            peers = targets.Values.ToArray();
            scopes.Clear();
            targets.Clear();
            pending.Clear();
        }
        await Task.WhenAll(notifications).ConfigureAwait(false);
        await CompleteAsync(session, peers).ConfigureAwait(false);
    }
}
