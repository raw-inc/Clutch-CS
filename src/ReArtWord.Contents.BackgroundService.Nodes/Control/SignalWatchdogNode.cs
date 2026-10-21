using System.Collections.Frozen;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>生存信号の欠落で一回の退避経路を開始し、復帰時にはその回の待受だけを終了します。</summary>
public sealed class SignalWatchdogNode : IResettableSourceNode
{
    /// <summary>通常の開始口に到着した信号を、稼働中は計時の更新として扱います。</summary>
    public IReadOnlySet<string> ResetInputs
    {
        get;
    } = new[] { "trigger" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>開始時点からの待機時間と、後続を開始する共通Signalを公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.signalWatchdog", "Signal Watchdog", "Control",
        """{"type":"object","properties":{"timeoutMs":{"x-runtime-input":false,"type":"integer","minimum":1,"maximum":2147483646,"default":5000}}}""",
        inputs: [], outputs: [], kind: ENodeKind.Source) with
    {
        Description = "Start monitoring when automatic execution begins, even before the first heartbeat. Each Start signal resets the timeout. After silence, emit Start once and own the downstream listeners. A returning signal stops only those listeners and rearms monitoring; already accepted work may finish. Use one monitoring peer independent of the main sender.",
    };

    /// <summary>一つの監視を複数PCで重複計時せず、入力発生元に監視場所を変動させません。</summary>
    /// <returns>監視設定または実行先の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.PcExecution == EPcExecutionMode.All || definition.Target is not null && (definition.Target.Scope != EExecutionScope.One || definition.Target.SourcePeer))
        {
            issues.Add(new("watchdog.target", "Choose one fixed monitoring peer or the coordinator, not the peer that emitted each heartbeat.", definition.Id));
        }
        return issues;
    }

    /// <summary>信号の本文は保持せず、最後の受信時刻と現在の退避範囲だけを所有します。</summary>
    /// <returns>監視終了と、その所有範囲の停止確認です。</returns>
    // 抑制理由: 一件の読取りTaskを期限を跨いで保持し、通常受信時またはfinallyの取消後に必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "一件の読取りTaskを保持し、通常受信時またはfinallyで必ずawaitして回収します。")]
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        if (context.Signals is not { } signals || context.Scopes is not { } scopes || 16 <= (context.Initial.SourceScopes?.Count ?? 0))
        {
            return NodeResult.Failure("watchdog.unavailable", "The monitoring runtime is unavailable or the nested scope limit was reached.", EOutcome.NotExecuted);
        }
        TimeProvider clock = context.Initial.TimeProvider;
        TimeSpan timeout = TimeSpan.FromMilliseconds(new NodeSettings(context.Initial.Settings).ReadInt32("timeoutMs", 5000));
        long latest = clock.GetTimestamp();
        SourceScope? fallback = null;
        Task<bool>? reading = null;
        NodeResult result = NodeResult.Success();
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        try
        {
            await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
            while (!context.IsStopRequested)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                bool received = false;
                while (signals.TryRead(out long arrived))
                {
                    latest = arrived;
                    received = true;
                }
                if (received && fallback is not null)
                {
                    SourceScope ended = fallback;
                    fallback = null;
                    result = await scopes.EndAsync(ended, CancellationToken.None).ConfigureAwait(false);
                    if (result.Outcome != EOutcome.Succeeded)
                    {
                        break;
                    }
                    await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                TimeSpan remaining = timeout - clock.GetElapsedTime(latest);
                if (fallback is null && remaining <= TimeSpan.Zero)
                {
                    NodeExecutionContext item = context.BeginItem();
                    fallback = scopes.Create(item.WorkflowId);
                    item = item with { SourceScopes = [.. item.SourceScopes ?? [], fallback] };
                    await context.EmitAsync(item, NodeResult.Success(), lifetime.Token).ConfigureAwait(false);
                    await context.ReportStatusAsync(new(ESourceState.Listening, "watchdog.timedOut", "The fallback path is active until the next heartbeat."), lifetime.Token).ConfigureAwait(false);
                }
                reading ??= signals.WaitToReadAsync(lifetime.Token).AsTask();
                if (fallback is not null || await WaitSignalAsync(reading, remaining, clock, lifetime.Token).ConfigureAwait(false))
                {
                    if (!await reading.ConfigureAwait(false))
                    {
                        break;
                    }
                    reading = null;
                }
            }
        }
        catch (OperationCanceledException) when (context.IsStopRequested && !cancellationToken.IsCancellationRequested)
        {
            // 正常な監視停止でも、finallyでこの監視が開始した退避待受を回収します。
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = NodeResult.Failure("watchdog.cancelled", "Signal monitoring was cancelled.", EOutcome.Cancelled);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (reading is not null)
            {
                try
                {
                    _ = await reading.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 所有する一件の読取り待ちを終了させます。
                }
            }
            if (fallback is not null)
            {
                NodeResult ended = await scopes.EndAsync(fallback, CancellationToken.None).ConfigureAwait(false);
                if (ended.Outcome != EOutcome.Succeeded)
                {
                    result = ended;
                }
            }
        }
        return result;
    }

    /// <summary>未完了の読取りは一件だけ再利用し、不要になった期限タイマーは都度回収します。</summary>
    /// <returns>期限より先に信号が到着したかです。</returns>
    private static async Task<bool> WaitSignalAsync(Task<bool> reading, TimeSpan remaining, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(remaining, clock, timer.Token);
        Task completed = await Task.WhenAny(reading, delay).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        try
        {
            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 信号受信後は古い期限のタイマーを残しません。
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(completed, reading);
    }
}
