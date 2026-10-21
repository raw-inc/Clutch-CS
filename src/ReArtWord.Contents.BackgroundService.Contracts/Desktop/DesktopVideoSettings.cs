namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>画面ごとの取得上限と映像の最大ビットレートです。</summary>
/// <param name="FramesPerSecond">1から120までの取得fpsです。</param>
/// <param name="MaximumBitrateKbps">100から100000までの映像kbpsです。VBRでは目標最大値です。</param>
/// <param name="HideCursor">この接続の映像にホスト側カーソルを合成しないかです。</param>
/// <param name="CodecDetails">追加の映像設定です。省略時は既存の低遅延方針です。</param>
/// <param name="VariableBitrate">目標最大値の範囲で映像に応じた可変ビットレートを使うかです。</param>
public sealed record DesktopVideoSettings(int FramesPerSecond, int MaximumBitrateKbps, bool HideCursor = false, DesktopCodecSettings? CodecDetails = null, bool VariableBitrate = false)
{
    /// <summary>固定長入力の末尾を読み、旧クライアントの既定値も維持します。</summary>
    /// <returns>検証済みの映像設定です。</returns>
    public static DesktopVideoSettings FromWire(int packed, bool vp9)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(packed & unchecked((int)0xe0000000), 0, nameof(packed));
        DesktopVideoSettings settings = new((packed >> 20) & 0x7f, packed & 0xfffff, (packed & 0x08000000) != 0, VariableBitrate: (packed & 0x10000000) != 0);
        if (packed == 0)
        {
            int bitrate = 12000;
            if (vp9)
            {
                bitrate = 8000;
            }
            settings = new(120, bitrate);
        }
        settings.Validate();
        return settings;
    }

    /// <summary>不正な入力をエンコーダ起動前に拒否します。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FramesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FramesPerSecond, 120);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBitrateKbps, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumBitrateKbps, 100000);
        if (VariableBitrate && CodecDetails?.Quality is not null)
        {
            throw new ArgumentException("VBR and an unbounded quality target cannot be selected together.");
        }
    }
}
