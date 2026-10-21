namespace ReArtWord.Contents.BackgroundService.Contracts.Desktop;

/// <summary>非圧縮PCMまたは有限ビットレートのOpusを選びます。</summary>
/// <param name="Codec">0はPCM、1はOpusです。</param>
/// <param name="BitrateKbps">PCMは0、Opusは提供するビットレートです。</param>
public sealed record DesktopAudioSettings(int Codec = 0, int BitrateKbps = 0)
{
    /// <summary>対話音声向けの選択可能なkbpsです。</summary>
    public static IReadOnlyList<int> OpusBitrates
    {
        get;
    } = Array.AsReadOnly(new[] { 16, 24, 32, 48, 64, 96, 128, 192, 256 });

    /// <summary>未対応方式や任意の符号化負荷を受け付けません。</summary>
    public void Validate()
    {
        if (Codec is < 0 or > 1 || Codec == 0 && BitrateKbps != 0 || Codec == 1 && !OpusBitrates.Contains(BitrateKbps))
        {
            throw new ArgumentException("Invalid desktop audio settings.");
        }
    }
}
