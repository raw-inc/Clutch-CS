using System.Text.Json;

using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>一件の開始時に固定した設定と入力です。</summary>
/// <param name="ExecutionId">ノード実行の識別子。</param>
/// <param name="CausationId">入力事象の識別子。</param>
/// <param name="WorkflowId">ワークフロー。</param>
/// <param name="Revision">定義の版。</param>
/// <param name="NodeId">ノード。</param>
/// <param name="PeerId">実行Peer。</param>
/// <param name="Settings">固定した設定。</param>
/// <param name="Inputs">固定した入力。</param>
/// <param name="TimeProvider">時刻の取得元。</param>
/// <param name="TimeZone">クラスタ共通のタイムゾーン。未指定の共通既定はUTCです。</param>
public sealed record NodeExecutionContext(
    Guid ExecutionId,
    Guid CausationId,
    string WorkflowId,
    string Revision,
    string NodeId,
    string PeerId,
    JsonElement Settings,
    IReadOnlyDictionary<string, NodeValue> Inputs,
    TimeProvider TimeProvider,
    TimeZoneInfo? TimeZone = null)
{
    /// <summary>ノード内で結果へ変換する例外も、秘密値保護を通して記録する先です。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Observability.IWorkflowLog? Log
    {
        get; init;
    }

    /// <summary>この一件の通常ログで自由文と値を伏せる指定です。</summary>
    public bool ContainsPersonalData
    {
        get; init;
    }

    /// <summary>進捗表示を所有するWF実行と、明示的に分離された反復要素です。</summary>
    public Contracts.Management.Preview.ProgressSession? Progress
    {
        get; init;
    }

    /// <summary>現在値の要求時評価です。対話型ノードの状態変更やSignal発火を行いません。</summary>
    public bool IsValueRead
    {
        get; init;
    }

    /// <summary>この経路で開始する待受が従う寿命です。受付済みの有限処理は継続できます。</summary>
    public IReadOnlyList<Contracts.Execution.SourceScope>? SourceScopes
    {
        get; init;
    }

    /// <summary>実際に処理するPCの識別子です。通信のPC別割り当てに使用します。</summary>
    public string? HostId
    {
        get; init;
    }

    /// <summary>この一件の設定が期待する動作契約。互換版を宣言する実装もこの期待を維持します。</summary>
    public int ContractVersion
    {
        get; init;
    } = 1;

    /// <summary>この入力までに通ったHookを保持し、ログや通知から同じHookを再度起動しません。</summary>
    public IReadOnlyList<Contracts.Observability.WorkflowHookStep>? Hooks
    {
        get; init;
    }

    /// <summary>時間指定から発生した入力の予定時刻と、未開始対象の扱い。</summary>
    public Scheduling.ScheduledOccurrence? Schedule
    {
        get; init;
    }

    /// <summary>取得開始時に固定したクラスタ共通運用版です。後続の設定変更を遡及しません。</summary>
    public Contracts.Configuration.Operational.ClusterOperationalSnapshot? Operational
    {
        get; init;
    }

    /// <summary>この一件で有効な詳細追跡方針です。</summary>
    public Contracts.Observability.Tracing.TracePolicy? TracePolicy
    {
        get; init;
    }

    /// <summary>通信ペイロードを秘密の保護境界へ渡す観測先です。</summary>
    public Observability.INodeTrace? Trace
    {
        get; init;
    }

    /// <summary>同一プロセス内で取得開始時のグラフを保持し、後続へ変更を遡及させません。</summary>
    internal Workflows.Compilation.CompiledWorkflow? CapturedWorkflow
    {
        get; init;
    }

    /// <summary>取得開始時に確認した発生元Peer実体を保持します。</summary>
    internal Contracts.Cluster.PeerInfo? CapturedPeer
    {
        get; init;
    }

    /// <summary>入力取得時に固定した全ノードの実行先です。後続の待機中に選び直しません。</summary>
    internal IReadOnlyDictionary<string, Workflows.Targets.TargetResolution>? CapturedTargets
    {
        get; init;
    }
}
