using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>再参加後のローカル変更を、人が比較選択または明示取消します。</summary>
/// <param name="OperationId">操作を再実施しない識別子です。</param>
/// <param name="WorkflowId">対象定義です。</param>
/// <param name="ExpectedLocalRevision">画面が確認したローカル版です。</param>
/// <param name="Resolutions">競合項目の選択です。</param>
/// <param name="Discard">共有へ反映せずローカル変更を明示取消する意思です。</param>
/// <param name="ActivationPolicy">統合保存後の反映方針です。</param>
/// <param name="ExpectedCurrentRevision">比較画面が確認した現在共有版です。nullは共有側不存在です。</param>
/// <param name="CurrentRevisionConfirmed">共有側不存在も含めた比較確認の意思です。</param>
public sealed record DetachedWorkflowResolution(Guid OperationId, string WorkflowId, string ExpectedLocalRevision,
    IReadOnlyDictionary<string, EEditResolution> Resolutions, bool Discard = false, EActivationPolicy ActivationPolicy = EActivationPolicy.Auto,
    string? ExpectedCurrentRevision = null, bool CurrentRevisionConfirmed = false);
