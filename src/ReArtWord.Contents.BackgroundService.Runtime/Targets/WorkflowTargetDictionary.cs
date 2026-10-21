using System.Collections.ObjectModel;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>一回の参加情報から作った固定実行先と、遅延PC候補用の同じ観測を保持します。</summary>
internal sealed class WorkflowTargetDictionary(IDictionary<string, TargetResolution> values, PcSelectionResolver resolver) : ReadOnlyDictionary<string, TargetResolution>(values)
{
    /// <summary>後から届く候補にも、同じ実行開始時のPeer実体と観測を適用します。</summary>
    internal PcSelectionResolver Resolver
    {
        get;
    } = resolver;

    /// <summary>入力値やPC情報を含めず、計画のノード数を表示します。</summary>
    /// <returns>固定した実行先の件数です。</returns>
    public override string ToString() => "Workflow targets: " + Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
