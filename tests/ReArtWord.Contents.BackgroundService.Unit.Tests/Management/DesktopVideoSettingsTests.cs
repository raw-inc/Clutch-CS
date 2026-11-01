using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>映像設定の通信境界と各エンコーダへの適用を確認します。</summary>
public sealed class DesktopVideoSettingsTests
{
    /// <summary>カーソル非表示を接続設定だけで適用し、既定の描画は維持します。</summary>
    [Theory]
    [InlineData(false, "1")]
    [InlineData(true, "0")]
    public void CursorVisibilityUsesAnIndependentWireFlag(bool hidden, string expected)
    {
        int packed = (30 << 20) | 6000;
        if (hidden)
        {
            packed |= 0x08000000;
        }
        DesktopVideoSettings settings = DesktopVideoSettings.FromWire(packed, false);
        Assert.Equal(new(30, 6000, hidden), settings);
        DesktopMonitor monitor = new(0, "test", -1920, -100, 1920, 1080, false);
        string[] arguments = DesktopEncoder.StartInfo("ffmpeg", monitor, "libx264", 20000, duplication: false, settings: settings).ArgumentList.ToArray();
        Assert.Equal(expected, arguments[Array.IndexOf(arguments, "-draw_mouse") + 1]);
        Assert.Equal("-1920", arguments[Array.IndexOf(arguments, "-offset_x") + 1]);
        Assert.Equal("-100", arguments[Array.IndexOf(arguments, "-offset_y") + 1]);
    }

    /// <summary>画面端の2px変化を最後のframeへ残し、同一frameだけを間引きます。</summary>
    /// <returns>実FFmpegのfilter出力の確認です。</returns>
    [ConfiguredToolTheory("CONTENTS_TEST_FFMPEG"), InlineData(0), InlineData(30), InlineData(62)]
    public async Task StaticTailPreservesSmallChangesAtTheDisplayEdgeAsync(int x)
    {
        string executable = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFMPEG")
            ?? throw new InvalidOperationException("The configured FFmpeg path is no longer available.");
        DesktopMonitor monitor = new(0, "test", 0, 0, 64, 64, true);
        System.Diagnostics.ProcessStartInfo start = DesktopEncoder.StartInfo(executable, monitor, "libx264", 20000, duplication: false);
        string filter = start.ArgumentList[start.ArgumentList.IndexOf("-vf") + 1];
        start.ArgumentList.Clear();
        string[] arguments = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            FormattableString.Invariant($"color=black:size=64x64:rate=30:duration=0.2,drawbox=x={x}:y=30:w=2:h=2:color=white:t=fill:enable='gte(n,3)'"),
            "-vf", filter, "-fps_mode", "vfr", "-f", "framemd5", "-"];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using System.Diagnostics.Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await ProcessCompletion.WaitAsync(process, timeout.Token).ConfigureAwait(true);
            string text = await output.ConfigureAwait(true);
            Assert.Equal(string.Empty, await error.ConfigureAwait(true));
            Assert.Equal(0, process.ExitCode);
            string[] frames = text.Split('\n').Where(line => line.StartsWith("0,", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.NotEqual(frames[0].Split(',')[^1], frames[1].Split(',')[^1]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(true);
            await Task.WhenAll(output, error).ConfigureAwait(true);
        }
    }

    /// <summary>旧入力の既定値と、新しい固定長入力の設定を確認します。</summary>
    [Fact]
    public void SettingsRoundTripAndRejectInvalidValues()
    {
        Assert.Equal(new DesktopVideoSettings(120, 12000), DesktopVideoSettings.FromWire(0, false));
        Assert.Equal(new DesktopVideoSettings(120, 8000), DesktopVideoSettings.FromWire(0, true));
        Assert.Equal(new DesktopVideoSettings(30, 2500), DesktopVideoSettings.FromWire((30 << 20) | 2500, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopVideoSettings.FromWire(-1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopVideoSettings.FromWire((121 << 20) | 2500, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopVideoSettings(30, 99).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopVideoSettings(30, 100001).Validate());
    }

    /// <summary>ソフトウェア方式にも最大rateを指定し、可変フレームの取得上限を維持します。</summary>
    /// <param name="encoder">実際の候補名です。</param>
    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    [InlineData("libx264")]
    [InlineData("vp9_qsv")]
    [InlineData("libvpx-vp9")]
    [InlineData("hevc_nvenc")]
    [InlineData("hevc_qsv")]
    [InlineData("hevc_amf")]
    [InlineData("libx265")]
    public void EveryEncoderReceivesRequestedLimits(string encoder)
    {
        DesktopMonitor monitor = new(0, "test", 0, 0, 1920, 1080, true);
        System.Diagnostics.ProcessStartInfo start = DesktopEncoder.StartInfo("ffmpeg", monitor, encoder, 20000, duplication: false, settings: new(30, 2500));
        string[] arguments = start.ArgumentList.ToArray();
        Assert.Equal("30", arguments[Array.IndexOf(arguments, "-framerate") + 1]);
        Assert.Equal("8", arguments[Array.IndexOf(arguments, "-g") + 1]);
        Assert.Equal("2500k", arguments[Array.IndexOf(arguments, "-maxrate") + 1]);
        Assert.Equal("625k", arguments[Array.IndexOf(arguments, "-bufsize") + 1]);
        Assert.Equal("vfr", arguments[Array.IndexOf(arguments, "-fps_mode") + 1]);
    }

    /// <summary>操作再開時の復号枚数と復号cacheの予算からGOPを制限します。</summary>
    [Theory, InlineData(30, 6000, 8), InlineData(120, 6000, 16), InlineData(30, 100000, 8), InlineData(1, 100, 1)]
    public void KeyFramesStayWithinFrameAndByteBudgets(int fps, int bitrate, int interval)
    {
        DesktopMonitor monitor = new(0, "test", 0, 0, 1920, 1080, true);
        string[] arguments = DesktopEncoder.StartInfo("ffmpeg", monitor, "hevc_nvenc", 20000, duplication: false, settings: new(fps, bitrate)).ArgumentList.ToArray();
        Assert.Equal(interval.ToString(System.Globalization.CultureInfo.InvariantCulture), arguments[Array.IndexOf(arguments, "-g") + 1]);
        Assert.Contains("-force_key_frames", arguments);
    }
}
