using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>離脱中のローカル変更と再参加後の比較状態です。</summary>
/// <param name="WorkflowId">対象定義です。</param>
/// <param name="Base">ローカル変更の元になった共有版です。</param>
/// <param name="Local">まだ共有済みでないローカル版です。</param>
/// <param name="Current">現在観測できる共有版です。</param>
/// <param name="Conflicts">利用者選択が必要な変更です。</param>
/// <param name="IntegrationOperationId">固定済みの元統合操作です。</param>
/// <param name="Integration">元統合結果です。</param>
/// <param name="IntegrationRetryAllowed">元変更が未実施と確定したため、新しい明示比較または取消を行えます。</param>
public sealed record DetachedWorkflowState(string WorkflowId, StoredWorkflow? Base, StoredWorkflow Local, StoredWorkflow? Current,
    IReadOnlyList<EditConflict> Conflicts, Guid? IntegrationOperationId = null, WorkflowSaveResult? Integration = null, bool IntegrationRetryAllowed = false);
