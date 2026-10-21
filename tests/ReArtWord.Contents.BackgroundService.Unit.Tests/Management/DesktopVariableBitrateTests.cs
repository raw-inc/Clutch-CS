using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>VBRの通信契約、品質指定との排他と実際のencoder引数を検証します。</summary>
public sealed class DesktopVariableBitrateTests
{
    /// <summary>映像・カーソルの値を壊さず独立したフラグを受信します。</summary>
    [Fact]
    public void WireFlagPreservesOtherSettingsAndRejectsQualityMode()
    {
        DesktopVideoSettings settings = DesktopVideoSettings.FromWire((30 << 20) | 6000 | 0x18000000, false);
        Assert.Equal(new(30, 6000, true, VariableBitrate: true), settings);
        Assert.False(DesktopVideoSettings.FromWire(0, false).VariableBitrate);
        Assert.Throws<ArgumentException>(() => (settings with { CodecDetails = new(8, null, 0, 23) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopVideoSettings.FromWire((30 << 20) | 6000 | 0x20000000, false));
    }

    /// <summary>最大目標と低遅延VBVを維持し、平均値を下げて可変rateを選びます。</summary>
    [Theory]
    [InlineData("h264_nvenc", "vbr")]
    [InlineData("hevc_nvenc", "vbr")]
    [InlineData("h264_amf", "vbr_peak")]
    [InlineData("hevc_amf", "vbr_peak")]
    [InlineData("h264_qsv", null)]
    [InlineData("hevc_qsv", null)]
    [InlineData("vp9_qsv", null)]
    [InlineData("libx264", null)]
    [InlineData("libx265", null)]
    [InlineData("libvpx-vp9", null)]
    public void EncodersReceiveVariableRateAndTargetMaximum(string encoder, string? rateControl)
    {
        foreach (DesktopCodecSettings? details in new DesktopCodecSettings?[] { null, new(8, null, 0, null) })
        {
            DesktopMonitor monitor = new(0, "test", 0, 0, 1920, 1080, true);
            string[] arguments = DesktopEncoder.StartInfo("ffmpeg", monitor, encoder, 20000, duplication: false,
                settings: new(30, 6000, CodecDetails: details, VariableBitrate: true)).ArgumentList.ToArray();
            Assert.Equal("3000k", arguments[Array.IndexOf(arguments, "-b:v") + 1]);
            Assert.Equal("6000k", arguments[Array.IndexOf(arguments, "-maxrate") + 1]);
            Assert.Equal("1500k", arguments[Array.IndexOf(arguments, "-bufsize") + 1]);
            Assert.DoesNotContain("-crf", arguments);
            if (rateControl is not null)
            {
                Assert.Equal(rateControl, arguments[Array.IndexOf(arguments, "-rc") + 1]);
            }
        }
    }

    /// <summary>GPUの優先関係を全H.26x候補で揃え、VP9は対応候補だけを使用します。</summary>
    [Fact]
    public void HardwareSelectionPrefersNvidiaThenAmdThenQsvThenSoftware()
    {
        Assert.Equal(["h264_nvenc", "h264_amf", "h264_qsv", "libx264"], DesktopEncoder.Candidates);
        Assert.Equal(["hevc_nvenc", "hevc_amf", "hevc_qsv", "libx265"], DesktopEncoder.HevcCandidates);
        Assert.Equal(["vp9_qsv", "libvpx-vp9"], DesktopEncoder.Vp9Candidates);
    }
}
