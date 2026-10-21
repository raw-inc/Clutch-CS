using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Processes;

/// <summary>登録プロセスに対する具体操作と継続監視を列挙します。</summary>
public static class ProcessNodeRegistration
{
    /// <summary>共通サービスにだけ操作を委譲するノード一覧を作ります。</summary>
    /// <returns>起動・停止・観測・復旧・準備待ちのノードです。</returns>
    public static IReadOnlyList<INode> Create(IProcessService service, IWorkflowEvents events)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(events);
        string basic = """{"type":"object","required":["processId"],"properties":{"processId":{"type":"string","minLength":1}}}""";
        string stop = """{"type":"object","required":["processId","failurePolicy"],"properties":{"processId":{"type":"string","minLength":1},"failurePolicy":{"type":"string","enum":["force","waitForManual"]},"gracefulTimeoutMs":{"type":"integer","minimum":1,"maximum":3600000,"default":30000}}}""";
        string suppression = """{"type":"object","required":["processId","suppressed"],"properties":{"processId":{"type":"string","minLength":1},"suppressed":{"type":"boolean"}}}""";
        return
        [
            new ProcessActionNode("process.start", basic, (id, _, token) => service.StartAsync(id, token)),
            new ProcessActionNode("process.stop", stop, (id, context, token) => StopAsync(service, id, context, cancellationToken: token)),
            new ProcessActionNode("process.restart", stop, (id, context, token) => StopAsync(service, id, context, restarting: true, cancellationToken: token)),
            new ProcessActionNode("process.observe", basic, (id, _, token) => service.ObserveAsync(id, token)),
            new ProcessActionNode("process.recover", basic, (id, _, token) => service.RecoverAsync(id, token)),
            new ProcessActionNode("process.signal", basic, (id, context, token) => SignalAsync(service, id, context, token), true),
            new ProcessActionNode("process.recoverySuppression", suppression, (id, context, token) => service.SetRecoverySuppressedAsync(id, new NodeSettings(context.Settings).ReadBoolean("suppressed"), token)),
            new ProcessActionNode("process.confirmRecovery", basic, (id, _, token) => service.ConfirmRecoveryAsync(id, token)),
            new ProcessReadyNode(service),
            new ProcessWatchNode(service, events),
            new ProcessLifecycleNode(service, events),
        ];
    }

    /// <summary>必須の停止後方針を実操作へ渡します。</summary>
    /// <returns>実終了を確認した結果です。</returns>
    private static ValueTask<ProcessOperationResult> StopAsync(IProcessService service, string id, NodeExecutionContext context, bool restarting = false, CancellationToken cancellationToken = default)
    {
        NodeSettings settings = new(context.Settings);
        EProcessStopPolicy policy = EProcessStopPolicy.WaitForManual;
        string force = "force";
        string? configured = settings.ReadText("failurePolicy");
        if (string.Equals(configured, force, StringComparison.Ordinal))
        {
            policy = EProcessStopPolicy.Force;
        }
        int timeout = settings.ReadInt32("gracefulTimeoutMs", 30000);
        return restarting ? service.RestartAsync(id, policy, timeout, cancellationToken)
                          : service.StopAsync(id, policy, timeout, cancellationToken);
    }

    /// <summary>現起動回を明示する条件判定入力をサービスへ渡します。</summary>
    /// <returns>旧起動回を拒否した保存結果です。</returns>
    private static ValueTask<ProcessOperationResult> SignalAsync(IProcessService service, string id, NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ProcessSignal signal = NodeSupport.Input(context)?.Json.Deserialize<ProcessSignal>(ContractJson.Options) ?? throw new ArgumentException("process状態入力が必要です。", nameof(context));
        return service.SignalAsync(id, signal, cancellationToken);
    }
}
