using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;

/// <summary>無効時は入力を記録せず、明示予約した次の一件だけを通常履歴から独立して保持します。</summary>
public sealed class WorkflowDebugRecorder(IPeerDirectory peers, NodeRegistry registry, TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, WorkflowDebugArm> armed = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, WorkflowDebugArm> tickets = [];
    private readonly Dictionary<Guid, WorkflowDebugCapture> captures = [];
    private readonly HashSet<Guid> cancelled = [];

    /// <summary>クラスタ全体の予約所有者へ一回分を要求します。</summary>
    public Func<WorkflowDebugArm, Guid, CancellationToken, ValueTask<bool?>>? Claim
    {
        get; set;
    }

    /// <summary>同じチケットの再送を新しい予約として扱いません。</summary>
    /// <returns>予約を保持できたかです。</returns>
    public bool Prepare(WorkflowDebugArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        using (gate.EnterScope())
        {
            Prune();
            if (tickets.TryGetValue(arm.Id, out WorkflowDebugArm? existing))
            {
                return existing == arm;
            }
            if (16 <= tickets.Count || arm.ExpiresAt <= clock.GetUtcNow())
            {
                return false;
            }
            tickets.Add(arm.Id, arm);
            armed[arm.WorkflowId] = arm;
            return true;
        }
    }

    /// <summary>記録の予約だけを取り消し、ワークフロー本体は停止しません。</summary>
    public void Cancel(Guid id)
    {
        using (gate.EnterScope())
        {
            if (tickets.ContainsKey(id))
            {
                cancelled.Add(id);
            }
            foreach (string key in armed.Where(pair => pair.Value.Id == id).Select(pair => pair.Key).ToArray())
            {
                armed.Remove(key);
            }
            if (captures.TryGetValue(id, out WorkflowDebugCapture? capture))
            {
                capture.Complete("debug.stopped");
            }
        }
    }

    /// <summary>予約されている場合だけクラスタへ問い合わせ、次の要求へ記録を固定します。</summary>
    /// <returns>取得権を確認できた一回だけの記録です。</returns>
    public async ValueTask<WorkflowDebugCapture?> BeginAsync(CompiledWorkflow workflow, Guid execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        WorkflowDebugArm? arm;
        using (gate.EnterScope())
        {
            if (!armed.TryGetValue(workflow.Definition.Id, out arm) || arm.ExpiresAt <= clock.GetUtcNow())
            {
                return null;
            }
        }
        bool? accepted;
        try
        {
            if (Claim is null)
            {
                return null;
            }
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            accepted = await Claim(arm, execution, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
        using (gate.EnterScope())
        {
            if (accepted is not null && armed.GetValueOrDefault(workflow.Definition.Id)?.Id == arm.Id)
            {
                armed.Remove(workflow.Definition.Id);
            }
        }
        if (accepted != true)
        {
            return null;
        }
        WorkflowDebugCapture capture = new(arm, workflow, registry, execution, peers.LocalPeerId, peers.LocalInstanceId, clock);
        using (gate.EnterScope())
        {
            Prune();
            if (cancelled.Contains(arm.Id))
            {
                return null;
            }
            captures[arm.Id] = capture;
        }
        return capture;
    }

    /// <summary>入力本体を含まないページだけを照会します。</summary>
    /// <returns>保持中の記録、または未取得・期限切れならnullです。</returns>
    public WorkflowDebugView? Read(WorkflowDebugQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using (gate.EnterScope())
        {
            Prune();
            return captures.GetValueOrDefault(query.Id)?.Read(query);
        }
    }

    /// <summary>予約は10分で、記録はその後20分までの一時保持に限ります。</summary>
    private void Prune()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (Guid id in tickets.Where(pair => pair.Value.ExpiresAt.AddMinutes(20) <= now).Select(pair => pair.Key).ToArray())
        {
            tickets.Remove(id);
            cancelled.Remove(id);
            if (captures.Remove(id, out WorkflowDebugCapture? capture))
            {
                capture.Complete("debug.expired");
            }
        }
        foreach (string key in armed.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            armed.Remove(key);
        }
    }
}
