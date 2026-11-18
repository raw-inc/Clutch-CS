using ReArtWord.Contents.BackgroundService.Contracts.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>検出開始時の照合方針と期限を固定し、引数そのものを重複保存しません。</summary>
internal sealed record ProcessReplacementWindow(DateTimeOffset Deadline, DateTimeOffset NotBefore, string ExecutablePath, string ArgumentsHash, EProcessRestartMatch Match,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? SessionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProcessWaitOrigin? WaitOrigin = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProcessWaitOrigin? AliveOrigin = null)
{
    /// <summary>利用者が復旧を確認した場合だけ、不明になった待機を終了します。実行前の後継照合条件は維持します。</summary>
    /// <returns>元の時計が有効なら同じ待機、不明なら明示確認を反映した待機です。</returns>
    internal ProcessReplacementWindow ConfirmUnknownWait(ProcessWaitClock clock)
    {
        if (clock.Remaining(WaitOrigin, Deadline).HasValue)
        {
            return this;
        }
        ProcessWaitOrigin now = clock.Capture();
        return this with { WaitOrigin = now, Deadline = now.Wall };
    }
}
