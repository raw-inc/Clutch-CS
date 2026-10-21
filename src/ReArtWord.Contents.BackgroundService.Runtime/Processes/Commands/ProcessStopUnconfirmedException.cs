namespace ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

/// <summary>取消要求後に子プロセスの終了を確認できなかったことを表します。</summary>
public sealed class ProcessStopUnconfirmedException : Exception
{
    /// <summary>停止確認不能を生成します。</summary>
    public ProcessStopUnconfirmedException()
    {
    }

    /// <summary>停止確認不能の説明を保持します。</summary>
    public ProcessStopUnconfirmedException(string message) : base(message)
    {
    }

    /// <summary>停止確認不能と内部原因を保持します。</summary>
    public ProcessStopUnconfirmedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
