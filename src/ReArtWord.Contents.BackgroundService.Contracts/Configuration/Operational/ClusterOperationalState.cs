using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>保存版とこのPeerで実際に有効な版を別々に表します。</summary>
/// <param name="Saved">共有保存した候補です。</param>
/// <param name="Effective">新規入力に現在適用する版です。</param>
/// <param name="SavedActivationPolicy">保存版に付随する適用・確認方針です。</param>
public sealed record ClusterOperationalState(ClusterOperationalSnapshot? Saved, ClusterOperationalSnapshot? Effective, EActivationPolicy SavedActivationPolicy = EActivationPolicy.Explicit);
