namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;

/// <summary>返信待ち上限を不正な受信データと区別します。</summary>
internal sealed class ReplyCapacityException : IOException
{
    /// <summary>送信元へ一時利用不能として返す原因を作成します。</summary>
    public ReplyCapacityException()
        : base("返信待ちhandleの上限に達しました。")
    {
    }

    /// <summary>利用不能の説明を指定します。</summary>
    public ReplyCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>利用不能の説明と内部原因を保持します。</summary>
    public ReplyCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
