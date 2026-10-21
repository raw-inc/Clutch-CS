using System.Diagnostics.CodeAnalysis;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting;

/// <summary>片方向終了後の中継タスクを回収し、解放用の取消しと実際の障害を区別します。</summary>
internal static class SessionRelay
{
    /// <summary>所有者が取り消した残り方向だけを正常解放として待ちます。</summary>
    /// <returns>全方向の終了確認です。実際の失敗や複数の障害は呼出元へ伝えます。</returns>
    // 抑制理由: 複数例外はcatchの直後でAggregateExceptionとして再送出し、致命的障害を隠しません。
    [SuppressMessage("Design", "CA1031", Justification = "全例外を直後で集約して送出します。正常取消しだけを回収します。")]
    internal static async Task DrainAsync(Task completion, CancellationToken cancellationToken = default)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completion.IsCanceled)
        {
            // 最初に終了した方向の成否は呼出元がawait済みです。残り方向の解放取消しだけを回収します。
        }
        catch (Exception) when (completion.Exception is { InnerExceptions.Count: > 1 })
        {
            // WhenAllの先頭例外だけにすると、別方向の致命的障害を隠すため全件を上位境界へ渡します。
        }
        if (completion.Exception is { InnerExceptions.Count: > 1 } failures)
        {
            throw failures;
        }
    }
}
