using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

/// <summary>人為停止と、元操作の寿命に束縛された一時続行です。</summary>
internal sealed record ManualSafetySetting(string WorkflowId, string Scope, bool Stopped, bool Continued = false,
    Guid OperationId = default, ManualSafetyAcceptance? Acceptance = null, ManualSafetyContinuation? Continuation = null, IReadOnlyList<Guid>? Supersedes = null);
