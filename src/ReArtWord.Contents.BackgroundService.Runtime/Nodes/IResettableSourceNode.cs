namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>稼働中の待受へ入力を通知し、再起動せずに計時を更新できるSourceです。</summary>
public interface IResettableSourceNode : ISourceNode
{
    /// <summary>自動開始の設定依存ではなく、稼働中への通知として扱う入力名です。</summary>
    public IReadOnlySet<string> ResetInputs
    {
        get;
    }
}
