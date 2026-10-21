namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Iteration;

/// <summary>入れ子を含む一件の実行全体の反復数と保持量を制限します。</summary>
internal sealed class IterationBudget
{
    private int remaining = 4096;
    private long bytes = 16777216;

    /// <summary>副作用を開始する前に一要素の枠を確保します。</summary>
    /// <returns>全体上限を超えない場合はtrueです。</returns>
    internal bool TryBegin() => 0 <= Interlocked.Decrement(ref remaining);

    /// <summary>結果と集約値を保持する前に容量を確保します。</summary>
    /// <returns>保持上限を超えない場合はtrueです。</returns>
    internal bool TryRetain(long size) => 0 <= Interlocked.Add(ref bytes, -size);
}
