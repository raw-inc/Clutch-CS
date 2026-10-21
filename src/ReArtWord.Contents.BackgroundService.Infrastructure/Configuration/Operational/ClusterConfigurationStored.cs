using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>保存した運用版と、その版の明示適用方針です。</summary>
/// <param name="Snapshot">保存版です。</param>
/// <param name="ActivationPolicy">保存時に固定した適用方針です。</param>
internal sealed record ClusterConfigurationStored(ClusterOperationalSnapshot Snapshot, EActivationPolicy ActivationPolicy);
