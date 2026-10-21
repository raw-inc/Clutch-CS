using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Data.Collections;

/// <summary>実行時に一つの添字を抽選し、同じ実行結果の参照中は抽選し直しません。</summary>
public sealed class RandomPickNode : IRuntimeValueNode
{
    /// <summary>抽選は静的評価できない値処理として公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("data.randomPick", "Random collection item", "Data", """{"type":"object","properties":{}}""",
        inputs: [new("value", EPortValueKind.Json, true)],
        outputs: [new("value", EPortValueKind.Json)]) with
        {
            IsValueOperation = true,
            Description = "Choose one array item or dictionary key-value pair uniformly when executed. Preserve values and types; duplicate array values remain separate candidates. Empty collections fail. Repeated executions may choose the same item."
        };

    /// <summary>抽選せずに設定だけを検証します。</summary>
    /// <returns>保存時に判定できる問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>入力を検証してから、一つの要素を抽選します。</summary>
    /// <returns>選択要素、または空の候補・不正入力の失敗です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            NodeValue collection = CollectionItemSelection.Read(context.Inputs);
            return ValueTask.FromResult(NodeResult.Success(CollectionItemSelection.Select(collection, System.Security.Cryptography.RandomNumberGenerator.GetInt32(CollectionItemSelection.Count(collection)))));
        }
        catch (FormatException error)
        {
            return ValueTask.FromResult(NodeResult.Failure("value." + error.Message, "RandomPick requires a non-empty JSON array or dictionary."));
        }
    }
}
