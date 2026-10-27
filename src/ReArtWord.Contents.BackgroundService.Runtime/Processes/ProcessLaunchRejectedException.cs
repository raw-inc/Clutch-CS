namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>アプリケーションを開始していないことが確定した起動拒否です。</summary>
public sealed class ProcessLaunchRejectedException : Exception
{
    /// <summary>OSへ起動を依頼した後の失敗かです。事前条件による拒否とは区別します。</summary>
    public bool LaunchAttempted
    {
        get;
    }

    /// <summary>副作用前の起動拒否を生成します。</summary>
    public ProcessLaunchRejectedException() : base("process.launchRejected")
    {
    }

    /// <summary>画面へ返す拒否理由を保持します。</summary>
    public ProcessLaunchRejectedException(string message) : base(message)
    {
    }

    /// <summary>副作用前のOS診断と拒否理由を保持します。</summary>
    public ProcessLaunchRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>プロセス未作成が確定したOSの拒否と、起動試行の有無を保持します。</summary>
    public ProcessLaunchRejectedException(string message, Exception innerException, bool launchAttempted) : base(message, innerException)
    {
        LaunchAttempted = launchAttempted;
    }

    /// <summary>標準の例外診断に、OSへの起動試行の有無を追加します。</summary>
    /// <returns>内部例外・スタックと試行状態を保持する診断です。</returns>
    public override string ToString() => FormattableString.Invariant($"{base.ToString()}{Environment.NewLine}LaunchAttempted: {LaunchAttempted}");
}
