using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>有限処理と継続処理に共通のノード登録契約です。</summary>
public interface INode
{
    /// <summary>選択画面と設定検証へ公開する定義です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>設定固有の意味を検証します。</summary>
    /// <returns>開始前に訂正が必要な問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition);
}
