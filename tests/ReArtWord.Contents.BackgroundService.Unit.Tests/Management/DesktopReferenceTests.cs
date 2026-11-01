using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実エンコーダで、静止時の無送信と操作再開時の短い参照を確認します。</summary>
public sealed class DesktopReferenceTests
{
    /// <summary>DDA取得後、別threadへ渡る入力がGPU surfaceではなく独立したCPU画素であることを実物で確認します。</summary>
    /// <returns>対話Windowsの取得graphをffprobeで開いた結果です。DDA非対応環境は対象外です。</returns>
    [DesktopDuplicationFact]
    public async Task DuplicationHandsOffCpuFramesBeforeEncodingAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        DesktopMonitor? monitor = DesktopDuplicationFactAttribute.FindMonitor();
        Assert.NotNull(monitor);
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, monitor, "hevc_nvenc", 20000, settings: new(120, 6000));
        string source = production.ArgumentList[production.ArgumentList.IndexOf("-i") + 1];
        ProcessStartInfo probe = new(files.Options.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", source, "-show_entries", "stream=codec_name,pix_fmt,width,height", "-of", "json" })
        {
            probe.ArgumentList.Add(argument);
        }
        using JsonDocument metadata = JsonDocument.Parse(await RunAsync(probe, timeout.Token).ConfigureAwait(true));
        JsonElement stream = Assert.Single(metadata.RootElement.GetProperty("streams").EnumerateArray());
        Assert.Equal("wrapped_avframe", stream.GetProperty("codec_name").GetString());
        Assert.Equal("bgra", stream.GetProperty("pix_fmt").GetString());
        Assert.Equal(monitor.Width, stream.GetProperty("width").GetInt32());
        Assert.Equal(monitor.Height, stream.GetProperty("height").GetInt32());
    }

    /// <summary>疎な更新は新しい参照で始まり、完全静止では重複フレームを増やしません。</summary>
    /// <returns>製品の符号化引数による実ビットストリームの検証です。</returns>
    [Theory]
    [InlineData("libx264", false)]
    [InlineData("libx265", false)]
    [InlineData("libvpx-vp9", false)]
    [InlineData("libx264", true)]
    [InlineData("libx265", true)]
    [InlineData("libvpx-vp9", true)]
    public async Task SparseChangesDoNotAccumulateLongReferencesAsync(string encoder, bool unchanged)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string path = Path.Combine(files.Root, "desktop.mkv");
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, new DesktopMonitor(0, "test", 0, 0, 64, 64, true),
            encoder, 20000, duplication: false, settings: new(30, 6000));
        string[] original = production.ArgumentList.ToArray();
        int start = Array.IndexOf(original, "-an"), end = Array.IndexOf(original, "-flush_packets");
        production.ArgumentList.Clear();
        string source = "testsrc2=size=64x64:rate=1:duration=4";
        if (unchanged)
        {
            source = "color=black:size=64x64:rate=30:duration=4";
        }
        foreach (string argument in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", source }.Concat(original[start..end]).Concat([path]))
        {
            production.ArgumentList.Add(argument);
        }
        await RunAsync(production, timeout.Token).ConfigureAwait(true);
        ProcessStartInfo probe = new(files.Options.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v", "-show_packets", "-show_entries", "packet=pts_time,flags", "-of", "json", path })
        {
            probe.ArgumentList.Add(argument);
        }
        using JsonDocument metadata = JsonDocument.Parse(await RunAsync(probe, timeout.Token).ConfigureAwait(true));
        JsonElement[] packets = metadata.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        int expected = 4;
        if (unchanged)
        {
            expected = 1;
        }
        Assert.Equal(expected, packets.Length);
        Assert.All(packets, packet => Assert.Contains('K', packet.GetProperty("flags").GetString()!));
    }

    /// <summary>実プロセスを有限時間で待ち、取消時も所有する子だけを回収します。</summary>
    /// <returns>標準出力です。</returns>
    private static async Task<string> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken = default)
    {
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await ProcessCompletion.WaitAsync(process, cancellationToken).ConfigureAwait(false);
            Assert.True(process.ExitCode == 0, await error.ConfigureAwait(false));
            return await output.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
