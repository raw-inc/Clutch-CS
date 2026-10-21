using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>新しい一件の受理時に固定する保存・一時追跡方針の境界です。</summary>
public interface ITracePolicyProvider
{
    /// <summary>現在取込済みの一時操作を保存通常方針へ重ねます。</summary>
    /// <returns>この一件に使用する方針です。</returns>
    public TracePolicy GetPolicy(string workflowId, TracePolicy saved);
}
