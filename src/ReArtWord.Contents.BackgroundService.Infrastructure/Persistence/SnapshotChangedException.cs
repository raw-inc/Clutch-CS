namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

/// <summary>固定した履歴が整理・置換され、同じ本文を保証できなくなったことを示します。</summary>
internal sealed class SnapshotChangedException : IOException
{
    /// <summary>元本文を検証できない固定書出しを中断します。</summary>
    public SnapshotChangedException() : base("The fixed snapshot content is no longer available.")
    {
    }

    /// <summary>内部の検証理由を保持します。</summary>
    public SnapshotChangedException(string message) : base(message)
    {
    }

    /// <summary>読取り元の失敗を内部保持します。</summary>
    public SnapshotChangedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
