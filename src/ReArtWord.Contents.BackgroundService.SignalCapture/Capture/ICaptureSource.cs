using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>OS の取得機構を画面と解析から分離します。</summary>
internal interface ICaptureSource : IDisposable
{
    /// <summary>バックエンド側で取得が継続しているか。</summary>
    public bool IsRunning
    {
        get;
    }

    /// <summary>キャプチャ対象を列挙します。</summary>
    /// <returns>取得可能なアダプタ。</returns>
    public IReadOnlyList<CaptureAdapter> GetAdapters();

    /// <summary>選択対象の取得を開始します。</summary>
    public void Start(CaptureAdapter adapter, Action<PacketRecord> receive);

    /// <summary>コールバックが終了するまで停止します。</summary>
    public void Stop();

    /// <summary>バックエンドの欠落数。</summary>
    public long Dropped
    {
        get;
    }

    /// <summary>取得範囲の照合により除外した通知数。キャプチャ欠落とは区別します。</summary>
    public long Suppressed
    {
        get;
    }

    /// <summary>非同期に発生した問題。</summary>
    public string Error
    {
        get;
    }
}
