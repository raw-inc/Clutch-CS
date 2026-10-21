using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>停止後も統計と開始失敗の理由を保持する取得元です。</summary>
internal sealed class ActiveSource
{
    private ICaptureSource? capture;
    private string error = "";

    /// <summary>要求された取得元。</summary>
    internal SourceRequest Request
    {
        get;
    }

    /// <summary>取得資源。開始できなかった場合は null。</summary>
    internal ICaptureSource? Capture
    {
        get => Volatile.Read(ref capture);
        set => Volatile.Write(ref capture, value);
    }

    /// <summary>初期化または終了の診断。</summary>
    internal string Error
    {
        get => Volatile.Read(ref error);
        set => Volatile.Write(ref error, value);
    }

    /// <summary>統計を残したまま、資源の二重解放を防ぐ状態。</summary>
    internal bool Closed
    {
        get; set;
    }

    /// <summary>取得元の指定を保持します。</summary>
    internal ActiveSource(SourceRequest request) => Request = request;

    /// <summary>現在状態をプロトコルへ変換します。</summary>
    /// <returns>取得元の状態。</returns>
    internal SourceState State()
    {
        string diagnostic = Error;
        if (diagnostic.Length == 0)
        {
            diagnostic = Capture?.Error ?? "";
        }
        return new SourceState(Request.Backend, Request.AdapterId, Capture?.IsRunning ?? false, diagnostic);
    }
}
