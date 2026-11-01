using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>詳細設定の通信境界と実ビットストリームに現れる上限を確認します。</summary>
public sealed class DesktopCodecTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>常設software候補と、明示的に実機試験を有効化したNVENC候補です。</summary>
    /// <returns>実符号化で確認するencoderです。</returns>
    public static IEnumerable<object[]> QualityEncoders()
    {
        yield return ["libx264"];
        yield return ["libx265"];
        yield return ["libvpx-vp9"];
        string enabled = "1";
        string? configured = Environment.GetEnvironmentVariable("CONTENTS_TEST_NVENC");
        if (string.Equals(configured, enabled, StringComparison.Ordinal))
        {
            yield return ["h264_nvenc"];
            yield return ["hevc_nvenc"];
        }
    }

    /// <summary>品質指定は旧kbpsに左右されず、品質値自体は実際の符号化結果へ反映されます。</summary>
    /// <returns>ビットレート欄だけを変えたpayloadの一致と品質値を変えた送信量の差です。</returns>
    [Theory, MemberData(nameof(QualityEncoders))]
    public async Task QualityIsIndependentOfBitrateAndChangesEncodedOutputAsync(string encoder)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        DesktopVideoSettings lowRate = new(30, 100, CodecDetails: new(60, null, 0, 18));
        DesktopVideoSettings highRate = lowRate with { MaximumBitrateKbps = 100000 };
        DesktopMonitor monitor = new(0, "test", 0, 0, 320, 180, true);
        string[] low = DesktopEncoder.StartInfo(files.Options.FfmpegPath, monitor, encoder, 20000, duplication: false, settings: lowRate).ArgumentList.ToArray();
        string[] high = DesktopEncoder.StartInfo(files.Options.FfmpegPath, monitor, encoder, 20000, duplication: false, settings: highRate).ArgumentList.ToArray();
        Assert.Equal(low, high);
        Assert.Equal("0", low[Array.IndexOf(low, "-b:v") + 1]);
        Assert.Equal("0", low[Array.IndexOf(low, "-maxrate") + 1]);
        Assert.Equal("0", low[Array.IndexOf(low, "-bufsize") + 1]);
        Assert.Equal(16, DesktopCodecOptions.KeyFrameLimit(lowRate));
        (string firstHash, long firstBytes) = await EncodePacketsAsync(files, encoder, lowRate, timeout.Token).ConfigureAwait(true);
        (string secondHash, long secondBytes) = await EncodePacketsAsync(files, encoder, highRate, timeout.Token).ConfigureAwait(true);
        (_, long lowerQualityBytes) = await EncodePacketsAsync(files, encoder, lowRate with { CodecDetails = new(60, null, 0, 42) }, timeout.Token).ConfigureAwait(true);
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(firstBytes, secondBytes);
        Assert.True(lowerQualityBytes < firstBytes);
        output.WriteLine($"{encoder}: Q18 / 100 kbps field={firstBytes} bytes, Q18 / 100000 kbps field={secondBytes} bytes, Q42={lowerQualityBytes} bytes");
    }

    /// <summary>製品の符号化設定で同一画像列を処理し、containerのランダムIDを除いて比較します。</summary>
    /// <returns>映像payloadのhash列と合計byte数です。</returns>
    private static async Task<(string Hashes, long Bytes)> EncodePacketsAsync(MediaTestFiles files, string encoder, DesktopVideoSettings settings, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(files.Root, Guid.NewGuid().ToString("N") + ".mkv");
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, new(0, "test", 0, 0, 320, 180, true), encoder, 20000, duplication: false, settings: settings);
        string[] original = production.ArgumentList.ToArray();
        int start = Array.IndexOf(original, "-an"), end = Array.IndexOf(original, "-flush_packets");
        production.ArgumentList.Clear();
        string[] source = ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=1"];
        foreach (string argument in source.Concat(original[start..end]).Concat([path]))
        {
            production.ArgumentList.Add(argument);
        }
        await RunAsync(production, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo probe = new(files.Options.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v", "-show_packets", "-show_entries", "packet=size,data_hash", "-show_data_hash", "sha256", "-of", "json", path })
        {
            probe.ArgumentList.Add(argument);
        }
        using JsonDocument result = JsonDocument.Parse(await RunAsync(probe, cancellationToken).ConfigureAwait(false));
        JsonElement[] packets = result.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        Assert.Equal(30, packets.Length);
        return (string.Join(',', packets.Select(packet => packet.GetProperty("data_hash").GetString())), packets.Sum(packet => long.Parse(packet.GetProperty("size").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>自動P・B無効・品質未指定を数値指定と区別します。</summary>
    [Fact]
    public void WireKeepsAutomaticAndDisabledSettingsDistinct()
    {
        byte[] input = new byte[24];
        int[] values = [9, 1, 60, -1, 0, -1];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(i * 4), values[i]);
        }
        Assert.Equal(new DesktopCodecSettings(60, null, 0), DesktopCodecOptions.Read(input));
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(16), 2);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(20), 23);
        Assert.Equal(new DesktopCodecSettings(60, 0, 2, 23), DesktopCodecOptions.Read(input));
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(4), 0);
        Assert.Throws<InvalidDataException>(() => DesktopCodecOptions.Read(input));
        input.AsSpan(8).Clear();
        Assert.Null(DesktopCodecOptions.Read(input));
        Assert.Throws<InvalidDataException>(() => DesktopCodecOptions.Read(input.AsSpan(0, 20)));
    }

    /// <summary>codecに合わない設定を実行前に拒否し、未対応encoderを黙って使いません。</summary>
    [Fact]
    public void ValidationAndSelectionPreserveRequestedBehavior()
    {
        new DesktopCodecSettings(240, null, 0, 63).Validate(true);
        new DesktopCodecSettings(240, 239, 4, 51).Validate(false);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(0, null, 0).Validate(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(241, null, 0).Validate(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(60, -1, 0).Validate(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(60, null, 5).Validate(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(60, null, 1).Validate(true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(60, null, 0, 52).Validate(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopCodecSettings(60, null, 0, 0).Validate(false));
        DesktopVideoSettings settings = new(30, 6000, CodecDetails: new(60, null, 2));
        Assert.True(DesktopCodecOptions.Supports("h264_nvenc", settings));
        Assert.True(DesktopCodecOptions.Supports("libx265", settings));
        Assert.False(DesktopCodecOptions.Supports("h264_qsv", settings));
        Assert.False(DesktopCodecOptions.Supports("hevc_amf", settings));
        Assert.True(DesktopCodecOptions.Supports("h264_qsv", settings with { CodecDetails = new(60, null, 0) }));
        Assert.False(DesktopCodecOptions.Supports("vp9_qsv", settings with { CodecDetails = new(60, null, 0, 30) }));
        Assert.Equal(60, DesktopCodecOptions.KeyFrameLimit(settings));
        Assert.Equal(5, DesktopCodecOptions.KeyFrameLimit(settings with { CodecDetails = new(60, 4, 2) }));
        Assert.Equal(15, DesktopCodecOptions.KeyFrameLimit(settings with { MaximumBitrateKbps = 100000, CodecDetails = new(240, null, 0) }));
    }

    /// <summary>B無効は実出力にもBを作らず、有効時は連続数・P数・I間隔を守ります。</summary>
    /// <returns>製品encoder引数を使った実FFmpeg/ffprobeの確認です。</returns>
    [Theory]
    [InlineData("libx264", 0, null, null, false)]
    [InlineData("libx264", 2, null, 23, false)]
    [InlineData("libx264", 2, 4, null, false)]
    [InlineData("libx265", 0, null, null, false)]
    [InlineData("libx265", 2, null, 23, false)]
    [InlineData("libx265", 2, 4, null, false)]
    [InlineData("libvpx-vp9", 0, null, 30, false)]
    [InlineData("libvpx-vp9", 0, 4, null, false)]
    [InlineData("libx264", 0, null, null, true)]
    [InlineData("libx264", 2, null, 23, true)]
    [InlineData("libx265", 2, null, 23, true)]
    public async Task EncodedFramesRespectExplicitLimitsAsync(string encoder, int b, int? p, int? quality, bool staticScreen)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string path = Path.Combine(files.Root, "details.mkv");
        DesktopVideoSettings settings = new(30, 6000, CodecDetails: new(12, p, b, quality));
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, new(0, "test", 0, 0, 128, 128, true), encoder, 20000, duplication: false, settings: settings);
        string[] original = production.ArgumentList.ToArray();
        int start = Array.IndexOf(original, "-an"), end = Array.IndexOf(original, "-flush_packets");
        production.ArgumentList.Clear();
        string[] source = ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=128x128:rate=30:duration=2"];
        if (staticScreen)
        {
            source[^1] = "color=black:size=128x128:rate=30:duration=2";
        }
        foreach (string argument in source.Concat(original[start..end]).Concat([path]))
        {
            production.ArgumentList.Add(argument);
        }
        await RunAsync(production, timeout.Token).ConfigureAwait(true);
        ProcessStartInfo probe = new(files.Options.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v", "-show_frames", "-show_entries", "frame=key_frame,pict_type", "-of", "json", path })
        {
            probe.ArgumentList.Add(argument);
        }
        using JsonDocument metadata = JsonDocument.Parse(await RunAsync(probe, timeout.Token).ConfigureAwait(true));
        JsonElement[] frames = metadata.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        int expectedFrames = 60;
        if (staticScreen && b == 0)
        {
            expectedFrames = 1;
        }
        Assert.Equal(expectedFrames, frames.Length);
        Assert.Equal(1, frames[0].GetProperty("key_frame").GetInt32());
        int sinceKey = 0, pCount = 0, consecutiveB = 0, totalB = 0;
        foreach (JsonElement frame in frames)
        {
            int key = frame.GetProperty("key_frame").GetInt32();
            if (key == 1)
            {
                sinceKey = pCount = consecutiveB = 0;
            }
            else
            {
                sinceKey++;
                string? type = frame.GetProperty("pict_type").GetString();
                switch (type)
                {
                    case "B": consecutiveB++; totalB++; break;
                    default: pCount++; consecutiveB = 0; break;
                }
                Assert.InRange(sinceKey, 1, DesktopCodecOptions.KeyFrameLimit(settings) - 1);
                Assert.InRange(pCount, 0, p ?? 11);
                Assert.InRange(consecutiveB, 0, b);
            }
        }
        if (0 < b)
        {
            Assert.True(0 < totalB);
        }
        else
        {
            Assert.Equal(0, totalB);
        }
    }

    /// <summary>VBRの製品引数で実符号化し、全フレームを復号可能なことを確認します。</summary>
    /// <returns>CPU各codecと、有効化したNVENCの実出力です。</returns>
    [Theory, MemberData(nameof(QualityEncoders))]
    public async Task VariableBitrateProducesDecodableVideoAsync(string encoder)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string path = Path.Combine(files.Root, "vbr.mkv");
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, new(0, "test", 0, 0, 256, 256, true), encoder, 20000,
            duplication: false, settings: new(30, 2000, VariableBitrate: true));
        string[] original = production.ArgumentList.ToArray();
        int start = Array.IndexOf(original, "-an"), end = Array.IndexOf(original, "-flush_packets");
        production.ArgumentList.Clear();
        string[] input = ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=256x256:rate=30:duration=1"];
        foreach (string argument in input.Concat(original[start..end]).Concat([path]))
        {
            production.ArgumentList.Add(argument);
        }
        await RunAsync(production, timeout.Token).ConfigureAwait(true);
        production.ArgumentList.Clear();
        foreach (string argument in new[] { "-nostdin", "-v", "error", "-i", path, "-f", "framemd5", "-" })
        {
            production.ArgumentList.Add(argument);
        }
        string decoded = await RunAsync(production, timeout.Token).ConfigureAwait(true);
        Assert.Equal(30, decoded.Split('\n').Count(line => line.StartsWith("0,", StringComparison.Ordinal)));
        output.WriteLine("{0}: VBR encode and 30 decoded frames verified", encoder);
    }

    /// <summary>伝送プリセットのfpsとGOPを実H.264へ適用し、復号したキーフレーム位置を確認します。</summary>
    /// <returns>全フレームと指定間隔のキーフレームの確認です。</returns>
    [Theory]
    [InlineData(8, 1024, 8)]
    [InlineData(30, 3072, 30)]
    [InlineData(30, 2048, 30)]
    [InlineData(30, 4096, 60)]
    [InlineData(30, 7168, 120)]
    [InlineData(60, 10240, 30)]
    public async Task TransmissionPresetKeyframesAreAppliedAsync(int fps, int bitrate, int interval)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        string path = Path.Combine(files.Root, "preset.mkv");
        int frameCount = interval * 3;
        DesktopVideoSettings settings = new(fps, bitrate, CodecDetails: new(interval, null, 0, null), VariableBitrate: true);
        ProcessStartInfo production = DesktopEncoder.StartInfo(files.Options.FfmpegPath, new(0, "test", 0, 0, 320, 180, true), "libx264", 20000,
            duplication: false, settings: settings);
        string[] original = production.ArgumentList.ToArray();
        int start = Array.IndexOf(original, "-an"), end = Array.IndexOf(original, "-flush_packets");
        production.ArgumentList.Clear();
        string[] input = ["-nostdin", "-v", "error", "-f", "lavfi", "-i", FormattableString.Invariant($"testsrc2=size=320x180:rate={fps}")];
        foreach (string argument in input.Concat(original[start..end]).Concat(["-frames:v", frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), path]))
        {
            production.ArgumentList.Add(argument);
        }
        await RunAsync(production, timeout.Token).ConfigureAwait(true);
        ProcessStartInfo probe = new(files.Options.FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v", "-show_frames", "-show_entries", "frame=key_frame", "-of", "json", path })
        {
            probe.ArgumentList.Add(argument);
        }
        using JsonDocument metadata = JsonDocument.Parse(await RunAsync(probe, timeout.Token).ConfigureAwait(true));
        JsonElement frames = metadata.RootElement.GetProperty("frames");
        Assert.Equal(frameCount, frames.GetArrayLength());
        IEnumerable<int> keys = frames.EnumerateArray().Select((frame, index) => (frame, index)).Where(item => item.frame.GetProperty("key_frame").GetInt32() == 1).Select(item => item.index);
        Assert.Equal(Enumerable.Range(0, 3).Select(index => index * interval), keys);
        output.WriteLine("{0} fps / {1} kbps / GOP {2}: {3} decoded frames verified", fps, bitrate, interval, frameCount);
    }

    /// <summary>有限時間で子を回収し、encoder診断を失敗理由に残します。</summary>
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
