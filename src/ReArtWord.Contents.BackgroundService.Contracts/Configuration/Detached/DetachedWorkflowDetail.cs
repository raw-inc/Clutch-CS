namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Detached;

/// <summary>比較可能な一件のローカル変更、または取得不能理由です。</summary>
public sealed record DetachedWorkflowDetail(DetachedWorkflowState? State, string? DetailReason = null);
