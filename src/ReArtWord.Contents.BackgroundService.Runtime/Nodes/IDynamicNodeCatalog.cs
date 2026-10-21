using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>クラスタ内で反映済みの定義を、固定組込みとは別のレシピとして解決します。</summary>
public interface IDynamicNodeCatalog
{
    /// <summary>現在のクラスタに公開できる実装を取得します。</summary>
    public IReadOnlyList<INode> Nodes
    {
        get;
    }

    /// <summary>既存呼出しの実行先を解決し、未登録の種類はnullとします。</summary>
    /// <returns>対応する実装です。</returns>
    public INode? Find(string type);

    /// <summary>配置したレシピの有効定義を実行前の展開へ渡します。</summary>
    /// <returns>同じクラスタの定義か、参照不能ならnullです。</returns>
    public Contracts.Workflow.WorkflowDefinition? Definition(string workflowId) => null;

    /// <summary>クラスタ内参照と公開契約を、保存前にも確認します。</summary>
    /// <returns>副作用なしで確認した定義の問題です。</returns>
    public IReadOnlyList<Contracts.Configuration.ValidationIssue> Validate(Contracts.Workflow.WorkflowDefinition definition);
}
