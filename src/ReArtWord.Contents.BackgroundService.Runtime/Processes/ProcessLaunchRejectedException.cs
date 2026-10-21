namespace ReArtWord.Contents.BackgroundService.Runtime.Processes;

/// <summary>アプリケーションを開始していないことが確定した起動拒否です。</summary>
public sealed class ProcessLaunchRejectedException : Exception
{
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
}
