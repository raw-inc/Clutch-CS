using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>検証が完了し実行順を決めた不変のワークフローです。</summary>
/// <param name="Definition">所有権を分離した定義。</param>
/// <param name="Revision">定義の内容識別子。</param>
/// <param name="OrderedNodes">設定接続を解決した実行用ノードの依存順。元の保存定義とは区別します。</param>
/// <param name="Incoming">ノードごとの入力接続。</param>
/// <param name="ConfigurationInputs">純粋な値だけから構築したSource設定入力。設定版ごとに固定します。</param>
/// <param name="ConfigurationValues">同じ設定版で評価した純粋値の結果。通常入力の供給に使い、実行契機にはしません。</param>
public sealed record CompiledWorkflow(
    WorkflowDefinition Definition,
    string Revision,
    IReadOnlyList<NodeDefinition> OrderedNodes,
    IReadOnlyDictionary<string, IReadOnlyList<WireDefinition>> Incoming,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, NodeValue>>? ConfigurationInputs = null,
    IReadOnlyDictionary<string, NodeResult>? ConfigurationValues = null)
{
    /// <summary>展開した配線とノードです。保存元の定義・版とは別に保持します。</summary>
    public WorkflowDefinition ExecutionDefinition => ExpandedDefinition ?? Definition;

    /// <summary>内部展開がある場合だけ保持する実行用グラフです。</summary>
    public WorkflowDefinition? ExpandedDefinition
    {
        get; init;
    }

    /// <summary>読み取り専用プレビューへ渡す配置単位のレイアウトです。</summary>
    public IReadOnlyList<WorkflowRecipeInstance> RecipeInstances
    {
        get; init;
    } = [];

    /// <summary>参照先変更で再構築不能な計画は、次の一件を開始しません。</summary>
    public IReadOnlyList<Contracts.Configuration.ValidationIssue> DependencyIssues
    {
        get; init;
    } = [];

    /// <summary>一件の開始時に解決するPC選択条件です。通常のデータ入力とは分けて保持します。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PcSelection>>? PcSelections
    {
        get; init;
    }

    /// <summary>開始信号の後で届いたPC候補を、その実行の入力から解決するノードです。</summary>
    internal IReadOnlySet<string> DynamicPcSelections
    {
        get; init;
    } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>PC候補を出力するノードは、別要求の保持値を次の実行先へ流用しません。</summary>
    internal IReadOnlySet<string> PcSelectionValues
    {
        get; init;
    } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>位置や安全方針を除いた待受設定とPC選択の識別子です。入力ごとの再計算を避けます。</summary>
    public IReadOnlyDictionary<string, string>? PcSelectionRevisions
    {
        get; init;
    }

    /// <summary>通常の実行経路と重複させない反復範囲です。</summary>
    internal IterationPlan? Iterations
    {
        get; init;
    }

    /// <summary>同じ反復要素の内部で先着値を選び、独立した後続実行へ分離しない合流です。</summary>
    internal IReadOnlySet<string> ScopedValueMerges
    {
        get; init;
    } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>反復内の公開入力から内部入口への制御依存です。ノードへ渡すデータ引数には含めません。</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> RecipeInputs
    {
        get; init;
    } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>通常の配線と配置の反復依存を同じ順序で待ちます。</summary>
    /// <returns>同じ親を重複しない実行上の依存です。</returns>
    internal IEnumerable<string> Parents(string node) => Incoming[node].Select(wire => wire.FromNode)
        .Concat(RecipeInputs.GetValueOrDefault(node) ?? []).Distinct(StringComparer.Ordinal);

    /// <summary>実行・受付には元の設定値ではなく解決済みのノードを参照します。</summary>
    /// <returns>この設定版の実行ノード、存在しなければnullです。</returns>
    public NodeDefinition? FindNode(string id) => OrderedNodes.FirstOrDefault(node => string.Equals(node.Id, id, StringComparison.Ordinal));
}
