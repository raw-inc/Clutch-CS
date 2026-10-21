using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>同時実行枠を保持する区間の開始と終了を、通常の実行境界で受け付けます。</summary>
public sealed class ConcurrencyLimitNode : IActionNode
{
    private readonly bool end;

    /// <summary>枠の取得・解放はグラフ全体を所有する実行器で行います。</summary>
    public ConcurrencyLimitNode(bool end = false)
    {
        this.end = end;
        string schema = """{"type":"object","properties":{"maxConcurrency":{"x-runtime-input":false,"type":"integer","minimum":1,"maximum":1024,"default":1},"waitTimeoutMs":{"x-runtime-input":false,"type":"integer","minimum":0,"maximum":2147483646,"default":30000},"timeoutMode":{"x-runtime-input":false,"type":"string","enum":["fail","proceed"],"default":"fail"}}}""";
        string type = ConcurrencyNodeTypes.Start;
        string name = "Concurrency Limit";
        PortDescriptor[] inputs = [new("value")];
        PortDescriptor[] outputs = [new("value"), new("start", EPortValueKind.Json), new(ConcurrencyNodeTypes.ScopePort, EPortValueKind.Json)];
        if (end)
        {
            type = ConcurrencyNodeTypes.End;
            name = "Concurrency Limit End";
            schema = """{"type":"object","properties":{}}""";
            inputs = [new(ConcurrencyNodeTypes.ScopePort, EPortValueKind.Json, Required: true), new("value")];
            outputs = [new("value")];
        }
        Descriptor = NodeSupport.Describe(type, name, "Control", schema, inputs, outputs) with { HasAsyncCompletion = true };
    }

    /// <summary>開始条件、任意の値、区間対応を公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>一つの区間を複数Peer上で重複して開始しません。</summary>
    /// <returns>設定と実行先の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.Target is not null && definition.Target.Scope != EExecutionScope.One)
        {
            issues.Add(new("concurrency.target", "Choose one peer for this boundary. The limit is shared across the cluster and the body may target multiple peers.", definition.Id));
        }
        return issues;
    }

    /// <summary>区間の入出力を確認し、枠の所有は後続を回収する実行器へ委ねます。</summary>
    /// <returns>入力値と区間開始の結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal) { ["value"] = NodeSupport.Input(context) ?? NodeValue.Null };
        if (!end)
        {
            outputs["start"] = NodeValue.FromObject(true);
            outputs[ConcurrencyNodeTypes.ScopePort] = NodeValue.FromObject(true);
        }
        return ValueTask.FromResult(new NodeResult(EOutcome.Succeeded, "success", outputs));
    }
}
