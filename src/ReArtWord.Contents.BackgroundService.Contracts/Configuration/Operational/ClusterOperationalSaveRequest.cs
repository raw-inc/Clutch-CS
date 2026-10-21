using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;

/// <summary>期待版と適用方針を伴う一回の共通設定保存です。</summary>
/// <param name="OperationId">再要求でも変更しない操作識別子です。</param>
/// <param name="Settings">共有する候補値です。</param>
/// <param name="ExpectedRevision">編集元の保存版、新規作成時はnullです。</param>
/// <param name="ActivationPolicy">スナップショット成功後の適用方針です。</param>
public sealed record ClusterOperationalSaveRequest(Guid OperationId, ClusterOperationalSettings Settings, string? ExpectedRevision, EActivationPolicy ActivationPolicy = EActivationPolicy.Auto);
