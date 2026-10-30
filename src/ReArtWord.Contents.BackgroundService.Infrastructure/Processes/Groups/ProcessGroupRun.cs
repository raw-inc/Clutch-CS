using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>再起動を越えて保持する、グループ操作の受付位置です。</summary>
internal sealed record ProcessGroupRun(Guid? OperationId = null, ProcessGroupDefinition? Plan = null, IReadOnlyList<ProcessGroupTarget>? Targets = null,
    EProcessGroupCommand Command = EProcessGroupCommand.Start, bool Automatic = false, bool RecoverOnly = false,
    EProcessGroupPhase Phase = EProcessGroupPhase.Idle, int Index = 0, DateTimeOffset? NextAt = null, DateTimeOffset? StartNotBefore = null,
    bool Claimed = false, EOutcome? Outcome = null, string? Reason = null, bool Suppressed = false)
{
    /// <summary>確認待ちを含め、個別操作と重ねない操作が残っているかです。</summary>
    internal bool Active => Phase != EProcessGroupPhase.Idle;

    /// <summary>内部の登録版や停止計画を公開状態へ投影します。</summary>
    /// <returns>画面とAPIで照会する操作状態です。</returns>
    internal ProcessGroupStatus Status(string id)
        => new(id, OperationId, Phase, Targets is not null && 0 <= Index && Index < Targets.Count ? Targets[Index].ApplicationId
                                                                                                  : null,
            NextAt, Outcome, Reason, Suppressed);
}
