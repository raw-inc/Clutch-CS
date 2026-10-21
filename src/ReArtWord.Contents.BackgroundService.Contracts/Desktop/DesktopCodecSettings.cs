namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>明示的な追加設定です。未指定なら既存の低遅延設定を使用します。</summary>
/// <param name="MaximumKeyFrameInterval">キーフレーム間隔の最大フレーム数です。</param>
/// <param name="MaximumPFrames">キーフレーム間のPフレーム数の上限です。nullは指定なしです。</param>
/// <param name="MaximumBFrames">連続Bフレーム数の上限です。VP9では0です。</param>
/// <param name="Quality">品質優先のCRF/CQ値です。nullはビットレート指定です。</param>
public sealed record DesktopCodecSettings(int MaximumKeyFrameInterval, int? MaximumPFrames, int MaximumBFrames, int? Quality = null)
{
    /// <summary>受信側の有限参照領域とエンコーダの共通範囲を超えません。</summary>
    public void Validate(bool vp9)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumKeyFrameInterval, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumKeyFrameInterval, 240);
        if (MaximumPFrames is int pFrames)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pFrames, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pFrames, 239);
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBFrames, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumBFrames, 4);
        if (vp9)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(MaximumBFrames, 0);
        }
        if (Quality is int quality)
        {
            int maximum = 51;
            if (vp9)
            {
                maximum = 63;
            }
            ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, maximum);
        }
    }
}
