using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>総件数と現在数をプレビューへ記録する、出力のない進捗表示です。</summary>
public sealed class ProgressBarNode(NodePreviewStore previews) : IActionNode
{
    /// <summary>型を固定せず二つの数値入力を受けます。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.progress", "ProgressBar", "Control", "{\"type\":\"object\",\"properties\":{\"progressScope\":{\"type\":\"string\",\"enum\":[\"workflow\",\"iteration\"],\"default\":\"workflow\"}}}",
        inputs: [new("total", Required: true), new("current", Required: true)], outputs: []) with { IsTerminal = true };

    /// <summary>設定を共通スキーマで検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>最新進捗だけを既存の有限プレビュー領域へ保持します。</summary>
    /// <returns>後続値を含まない取得結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        ProgressValue? progress = ProgressValue.Read(NodeSupport.Input(context, "total"), NodeSupport.Input(context, "current"));
        if (progress is null)
        {
            previews.PublishProgress(context, null, "progress.invalid");
            return ValueTask.FromResult(NodeResult.Failure("progress.invalid", "Progress requires two non-negative finite numbers."));
        }
        previews.PublishProgress(context, progress);
        return ValueTask.FromResult(NodeResult.Success());
    }
}
