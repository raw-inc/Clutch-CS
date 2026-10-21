using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>配送をまだ開始していない予定入力を、設定変更や入力元PCの停止後も識別して保持します。</summary>
/// <param name="Id">一件の実行を識別する元の因果ID。</param>
/// <param name="ExecutionId">Sourceがこの入力を取得したときの実行ID。</param>
/// <param name="Definition">取得時のワークフロー定義。</param>
/// <param name="Revision">取得時の内容版。</param>
/// <param name="Source">元の待受の識別と状態。</param>
/// <param name="Settings">入力取得時に固定したSource設定。</param>
/// <param name="Inputs">入力取得時に固定したSource入力。</param>
/// <param name="Result">実際に取得した値と結果。</param>
/// <param name="TimeZoneId">取得時の共通タイムゾーン。</param>
/// <param name="Operational">取得時の共通運用設定。</param>
/// <param name="Trace">取得時の追跡方針。</param>
/// <param name="SourcePeer">取得時の入力元Peer実体。</param>
/// <param name="Targets">その回に固定した各ノードの対象。</param>
/// <param name="Schedule">予定時刻と未開始対象の扱い。</param>
/// <param name="Hooks">この入力に至るまでのHookの通過履歴。</param>
public sealed record ScheduleInputSnapshot(Guid Id, Guid ExecutionId, WorkflowDefinition Definition, string Revision, SourceExecutionInfo Source,
    JsonElement Settings, IReadOnlyDictionary<string, NodeValue> Inputs, NodeResult Result, string TimeZoneId, ClusterOperationalSnapshot? Operational,
    TracePolicy? Trace, PeerInfo? SourcePeer, IReadOnlyDictionary<string, TargetResolution> Targets, ScheduledOccurrence Schedule, IReadOnlyList<WorkflowHookStep>? Hooks)
{
    /// <summary>取得済みの入力と設定を固定し、元Sourceの参照を出力にも含めます。</summary>
    /// <returns>実行し直す必要のない、取得済み入力の保存内容。</returns>
    internal static ScheduleInputSnapshot Capture(SourceExecutionInfo source, NodeEmission emission)
    {
        NodeExecutionContext context = emission.Context;
        CompiledWorkflow plan = context.CapturedWorkflow ?? throw new InvalidOperationException("予定入力の取得時定義がありません。");
        ScheduledOccurrence schedule = context.Schedule ?? throw new InvalidOperationException("予定入力の時刻と方針がありません。");
        IReadOnlyDictionary<string, TargetResolution> targets = context.CapturedTargets ?? throw new InvalidOperationException("予定入力の固定対象がありません。");
        SourceExecutionInfo original = source with { Revision = context.Revision, Result = null };
        Dictionary<string, NodeValue> outputs = new(emission.Result.Outputs, StringComparer.Ordinal) { ["source"] = NodeValue.FromObject(original) };
        return new(context.CausationId, context.ExecutionId, plan.Definition, context.Revision, original, context.Settings, context.Inputs,
            emission.Result with { Outputs = outputs }, context.TimeZone?.Id ?? "UTC", context.Operational, context.TracePolicy, context.CapturedPeer, targets, schedule, context.Hooks);
    }

    /// <summary>取得時と同じ版を確認して文脈を再構成します。Sourceから値を再取得しません。</summary>
    /// <returns>元の因果IDと固定対象を持つ入力。</returns>
    public NodeEmission Restore(CompiledWorkflow plan, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(clock);
        if (!string.Equals(plan.Revision, Revision, StringComparison.Ordinal) || !string.Equals(plan.Definition.Id, Definition.Id, StringComparison.Ordinal)
            || plan.FindNode(Source.NodeId) is null || Id == Guid.Empty || Source.Id != Schedule.SourceId)
        {
            throw new InvalidOperationException("保持した予定入力の識別または設定版が一致しません。");
        }
        NodeExecutionContext context = new(ExecutionId, Id, Definition.Id, Revision, Source.NodeId, Source.PeerId, Settings, Inputs, clock, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId))
        {
            CapturedWorkflow = plan,
            CapturedPeer = SourcePeer,
            CapturedTargets = Targets,
            Operational = Operational,
            TracePolicy = Trace,
            Hooks = Hooks,
            Schedule = Schedule,
        };
        return new(context, Result);
    }
}
