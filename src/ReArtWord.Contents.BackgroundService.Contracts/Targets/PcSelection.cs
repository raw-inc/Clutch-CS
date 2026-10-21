namespace ReArtWord.Contents.BackgroundService.Contracts.Targets;

/// <summary>実行計画の確定時に評価するPCの選択条件です。観測結果は保持しません。</summary>
/// <param name="HostIds">明示的に選んだPCの識別子。</param>
/// <param name="AllHosts">現在のクラスタに所属する全PCを選ぶか。</param>
/// <param name="Lowest">指定したリソースの値が最小のPCを選ぶ条件。</param>
/// <param name="Within">低負荷選択の比較対象。省略時は現在のクラスタ全体。</param>
public sealed record PcSelection(IReadOnlyList<string>? HostIds = null, bool AllHosts = false,
    PcResourceChoice? Lowest = null, IReadOnlyList<PcSelection>? Within = null);
