using System.Globalization;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Packets;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture.Tests;

/// <summary>実デバイスを使わず、通知と停止を観測する取得元。</summary>
internal sealed class TestCapture : ICaptureSource
{
    private Action<PacketRecord>? receive;
    private readonly Action? beforeEnumeration;
    private readonly Action? beforeStop;

    /// <summary>ネイティブ初期化・停止の待機を外部資源なしで再現します。</summary>
    internal TestCapture(Action? beforeEnumeration = null, Action? beforeStop = null)
    {
        this.beforeEnumeration = beforeEnumeration;
        this.beforeStop = beforeStop;
    }

    /// <summary>停止の観測値。</summary>
    internal int Stops
    {
        get; private set;
    }

    /// <summary>解放の観測値。</summary>
    internal int Disposals
    {
        get; private set;
    }

    /// <summary>通知可能か。</summary>
    public bool IsRunning => receive is not null;

    /// <summary>模擬ネイティブ欠落。</summary>
    public long Dropped => 2;

    /// <summary>模擬観測点除外。</summary>
    public long Suppressed => 3;

    /// <summary>診断。</summary>
    public string Error => "";

    /// <summary>複数取得元試験用の ID。</summary>
    /// <returns>模擬取得元一覧。</returns>
    public IReadOnlyList<CaptureAdapter> GetAdapters()
    {
        beforeEnumeration?.Invoke();
        return Enumerable.Range(0, 16).Select(index => new CaptureAdapter("demo" + index.ToString(CultureInfo.InvariantCulture), "Test", "")).ToArray();
    }

    /// <summary>通知先を保持します。</summary>
    public void Start(CaptureAdapter adapter, Action<PacketRecord> receive) => this.receive = receive;

    /// <summary>原本を投入します。</summary>
    internal void Emit(PacketRecord packet) => receive?.Invoke(packet);

    /// <summary>通知先を切り離します。</summary>
    public void Stop()
    {
        beforeStop?.Invoke();
        Stops++;
        receive = null;
    }

    /// <summary>解放呼び出しを数えます。</summary>
    public void Dispose()
    {
        Disposals++;
        receive = null;
    }
}
