namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Annotations;

/// <summary>同一対話セッションの一時的な指示表示です。座標は仮想desktopの物理pixelです。</summary>
internal sealed record DesktopAnnotationSnapshot(long Revision, long Now, bool Owner, long[][] Cursors, long[][] Points)
{
    /// <summary>各点の作成から消えるまでの時間です。</summary>
    internal static int Duration => 4000;

    /// <summary>通信は時刻差と整数配列だけを送り、接続識別子や名前を公開しません。</summary>
    /// <returns>cursor=[接続色, x, y]、point=[連番,色,線,x,y,経過ms]の有限なpayloadです。</returns>
    internal object Payload() => new
    {
        duration = Duration,
        cursors = Cursors,
        points = Points.Select(point => new[] { point[0], point[1], point[2], point[3], point[4], Math.Max(0, Now - point[5]) }),
    };
}
