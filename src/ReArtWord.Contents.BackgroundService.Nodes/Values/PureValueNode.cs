using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Values;

/// <summary>通常実行と設定用評価を同じ副作用なしの構築へ接続します。</summary>
public abstract class PureValueNode : IValueNode
{
    /// <summary>型と設定の公開契約です。</summary>
    public abstract NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>公開した共通設定スキーマを検証します。</summary>
    /// <returns>設定の不一致です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            return NodeSupport.Validate(definition, Descriptor);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return [new("value.settings.invalid", "Settings contain an invalid JSON string or value.", definition.Id)];
        }
    }

    /// <summary>入力ごとに同じ純粋評価を実行します。</summary>
    /// <returns>構築された値または失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(new(context.NodeId, Descriptor.Type, context.Settings, ContractVersion: context.ContractVersion), context.Inputs));
    }

    /// <summary>保存・通信・時刻依存を含まない構築を行います。</summary>
    /// <returns>確認済みの構築結果です。</returns>
    public NodeResult Evaluate(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputs);
        IReadOnlyList<ValidationIssue> issues = Validate(definition);
        if (issues.Count != 0)
        {
            return NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
        }
        try
        {
            return NodeResult.Success(Build(definition, inputs));
        }
        catch (FormatException error)
        {
            return NodeResult.Failure("value." + error.Message, "The value does not satisfy the selected type or construction policy.");
        }
        catch (Exception error) when (error is JsonException or OverflowException or ArgumentException or InvalidOperationException)
        {
            return NodeResult.Failure("value.invalid", "The value or type definition cannot be constructed without loss.");
        }
    }

    /// <summary>ノード固有の純粋構築です。</summary>
    /// <returns>独立した所有値です。</returns>
    protected abstract NodeValue Build(NodeDefinition definition, IReadOnlyDictionary<string, NodeValue> inputs);
}
