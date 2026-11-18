namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>日時は表示用に保存し、相対待機の残りはOS起動回と稼働時間で計算します。</summary>
internal sealed class ProcessWaitClock
{
    /// <summary>OSのプロセス開始時刻と比較する経過時間は、ワークフローの時計とは独立して取得します。</summary>
    internal static ProcessWaitClock OperatingSystemClock
    {
        get;
    } = new(TimeProvider.System, SystemBootObservation.Read);

    private static string RuntimePrefix => "runtime:";
    private readonly TimeProvider clock;
    private readonly SystemBootSession boot;
    private readonly long started;

    /// <summary>OS起動回が利用できない環境では、このサービスの寿命内だけ同じ時計として扱います。</summary>
    internal ProcessWaitClock(TimeProvider clock, Func<SystemBootSession?> readBoot)
    {
        this.clock = clock;
        started = clock.GetTimestamp();
        boot = readBoot() ?? new(RuntimePrefix + Guid.NewGuid().ToString("N"), TimeSpan.Zero);
    }

    /// <summary>待機の起点です。OS起動中にCoreだけ再起動しても同じ時間軸を使用します。</summary>
    /// <returns>起動回と二つの時計の観測値です。</returns>
    internal ProcessWaitOrigin Capture() => new(boot.Id, boot.Uptime + clock.GetElapsedTime(started), clock.GetUtcNow());

    /// <summary>OSが再起動したときも、今回のOS稼働時間だけで元の待機が満了したと証明できるまで開始しません。</summary>
    /// <returns>残り時間です。起点を照合できない場合はnullです。</returns>
    internal TimeSpan? Remaining(ProcessWaitOrigin? origin, DateTimeOffset? deadline)
    {
        if (deadline is null)
        {
            return TimeSpan.Zero;
        }
        if (origin is not { } saved)
        {
            return null;
        }
        ProcessWaitOrigin now = Capture();
        TimeSpan elapsed = now.Uptime;
        if (string.Equals(now.BootId, saved.BootId, StringComparison.Ordinal))
        {
            elapsed -= saved.Uptime;
        }
        else if (now.BootId.StartsWith(RuntimePrefix, StringComparison.Ordinal) || saved.BootId.StartsWith(RuntimePrefix, StringComparison.Ordinal))
        {
            return null;
        }
        if (elapsed < TimeSpan.Zero)
        {
            return null;
        }
        return deadline - saved.Wall - elapsed;
    }
}
