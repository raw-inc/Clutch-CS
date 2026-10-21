using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>最後に成立した一件の文脈と、所属変更による予定終了を保持します。</summary>
internal sealed class SourceCaptureState(NodeExecutionContext initial)
{
    private NodeExecutionContext last = initial;
    private bool ended;

    /// <summary>終了通知にも使う最後の成立済み文脈です。</summary>
    public NodeExecutionContext Last
    {
        get => Volatile.Read(ref last);
        set => Volatile.Write(ref last, value);
    }

    /// <summary>次入力の許可が失われた場合に終了結果へ保持する拒否理由です。</summary>
    public NodeResult? Rejection
    {
        get; set;
    }

    /// <summary>新しい一件を開始せず予定終了へ移ったか。</summary>
    public bool Ended
    {
        get => Volatile.Read(ref ended);
        set => Volatile.Write(ref ended, value);
    }
}
