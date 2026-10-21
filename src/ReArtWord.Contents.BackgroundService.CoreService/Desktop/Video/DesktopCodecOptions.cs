using System.Buffers.Binary;
using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;

/// <summary>追加設定を有限の受信契約と対応済みencoderのオプションへ変換します。</summary>
internal static class DesktopCodecOptions
{
    private static readonly string[] detailedEncoders = ["h264_nvenc", "hevc_nvenc", "libx264", "libx265", "libvpx-vp9"];

    /// <summary>qualityとB-frameの明示指定を黙って無視する候補を選びません。</summary>
    /// <returns>この候補で要求を試行できるかです。</returns>
    internal static bool Supports(string encoder, DesktopVideoSettings? settings)
        => settings?.CodecDetails is not { } details || (details.Quality is null && details.MaximumBFrames == 0) || detailedEncoders.Contains(encoder, StringComparer.Ordinal);

    /// <summary>24byteの追加設定です。kind 9、使用有無、I間隔、P上限、B上限、品質値を保持します。</summary>
    /// <returns>次の映像選択に適用する設定です。品質-1はビットレート指定です。</returns>
    internal static DesktopCodecSettings? Read(ReadOnlySpan<byte> message)
    {
        if (message.Length != 24 || BinaryPrimitives.ReadInt32BigEndian(message) != 9)
        {
            throw new InvalidDataException("Invalid desktop codec settings.");
        }
        int enabled = BinaryPrimitives.ReadInt32BigEndian(message[4..]);
        if (enabled == 0 && !message[8..].ContainsAnyExcept((byte)0))
        {
            return null;
        }
        if (enabled != 1)
        {
            throw new InvalidDataException("Invalid desktop codec settings mode.");
        }
        int quality = BinaryPrimitives.ReadInt32BigEndian(message[20..]);
        int? selectedQuality = quality;
        if (quality == -1)
        {
            selectedQuality = null;
        }
        int? pFrames = BinaryPrimitives.ReadInt32BigEndian(message[12..]);
        if (pFrames == -1)
        {
            pFrames = null;
        }
        return new(BinaryPrimitives.ReadInt32BigEndian(message[8..]), pFrames, BinaryPrimitives.ReadInt32BigEndian(message[16..]), selectedQuality);
    }

    /// <summary>P上限を超えるGOPを作らず、有限の圧縮参照cacheへ収めます。</summary>
    /// <returns>今回の最大GOP長です。</returns>
    internal static int KeyFrameLimit(DesktopVideoSettings settings)
    {
        int bytes = Math.Max(1, (int)(6L * 1024 * 1024 * 8 * settings.FramesPerSecond / (settings.MaximumBitrateKbps * 1000L)));
        int frames = Math.Clamp((settings.FramesPerSecond + 3) / 4, 1, 16);
        if (settings.CodecDetails is { } details)
        {
            // Bを使わない場合も含め、Pの最大数を保証する保守的なGOP上限です。
            frames = details.MaximumKeyFrameInterval;
            if (details.MaximumPFrames is int pFrames)
            {
                frames = Math.Min(frames, pFrames + 1);
            }
        }
        if (settings.CodecDetails?.Quality is not null)
        {
            // 品質指定では送信量を予測できません。未使用のkbpsではなく、再復号の枚数を制限します。
            return Math.Clamp(frames, 1, 16);
        }
        return Math.Clamp(frames, 1, bytes);
    }

    /// <summary>通常設定を維持し、明示された追加項目だけ置き換えます。</summary>
    internal static void Apply(List<string> arguments, string encoder, DesktopVideoSettings settings)
    {
        if (settings.VariableBitrate)
        {
            // 平均目標を最大目標より低くし、QSVでもCBRの自動選択を避けます。
            // VBVは短時間の平均を制御するため、瞬間的な通信帯域の上限ではありません。
            Set(arguments, "-b:v", (settings.MaximumBitrateKbps / 2).ToString(CultureInfo.InvariantCulture) + "k");
            int crf = arguments.IndexOf("-crf");
            if (0 <= crf)
            {
                arguments.RemoveRange(crf, 2);
            }
            switch (encoder)
            {
                case "h264_nvenc":
                case "hevc_nvenc": Set(arguments, "-rc", "vbr"); break;
                case "h264_amf":
                case "hevc_amf": Set(arguments, "-rc", "vbr_peak"); break;
            }
        }
        if (settings.CodecDetails is not { } details)
        {
            return;
        }
        bool vp9 = DesktopEncoder.Vp9Candidates.Contains(encoder, StringComparer.Ordinal);
        details.Validate(vp9);
        string interval = (KeyFrameLimit(settings) / (double)settings.FramesPerSecond).ToString("0.######", CultureInfo.InvariantCulture);
        Set(arguments, "-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t-prev_forced_t," + interval + "))");
        if (!vp9)
        {
            Set(arguments, "-bf", details.MaximumBFrames.ToString(CultureInfo.InvariantCulture));
            Set(arguments, "-flags", "+cgop");
        }
        if (0 < details.MaximumBFrames)
        {
            string delay = (details.MaximumBFrames + 1).ToString(CultureInfo.InvariantCulture);
            switch (encoder)
            {
                case "h264_nvenc":
                case "hevc_nvenc": Set(arguments, "-tune", "ll"); Set(arguments, "-zerolatency", "0"); Set(arguments, "-delay", delay); Set(arguments, "-b_adapt", "0"); break;
                case "libx264": Set(arguments, "-tune", "fastdecode"); Set(arguments, "-x264-params", "repeat-headers=1:open-gop=0:scenecut=0:rc-lookahead=0:sync-lookahead=0:b-adapt=0:b-pyramid=none"); break;
                case "libx265": Set(arguments, "-tune", "fastdecode"); Set(arguments, "-x265-params", "repeat-headers=1:open-gop=0:scenecut=0:rc-lookahead=" + delay + ":frame-threads=1:pools=4:b-adapt=0:b-pyramid=0"); break;
            }
        }
        if (details.Quality is int quality)
        {
            // CQ/CRFを以前のビットレートに拘束しません。VP9の純粋な品質指定にも全ての解除が必要です。
            Set(arguments, "-b:v", "0");
            Set(arguments, "-maxrate", "0");
            Set(arguments, "-bufsize", "0");
            string value = quality.ToString(CultureInfo.InvariantCulture);
            switch (encoder)
            {
                case "h264_nvenc":
                case "hevc_nvenc": Set(arguments, "-rc", "vbr"); Set(arguments, "-cq", value); break;
                case "libx264":
                case "libx265": Set(arguments, "-crf", value); break;
                case "libvpx-vp9": Set(arguments, "-crf", value); break;
                default: throw new ArgumentException("The encoder does not support the requested quality mode.", nameof(encoder));
            }
        }
        else
        {
            string x264 = "libx264";
            if (string.Equals(encoder, x264, StringComparison.Ordinal))
            {
                int crf = arguments.IndexOf("-crf");
                if (0 <= crf)
                {
                    arguments.RemoveRange(crf, 2);
                }
                if (!settings.VariableBitrate)
                {
                    Set(arguments, "-b:v", settings.MaximumBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
                }
            }
        }
    }

    /// <summary>同じFFmpegオプションを重複させません。</summary>
    private static void Set(List<string> arguments, string key, string value)
    {
        int index = arguments.IndexOf(key);
        if (index < 0)
        {
            arguments.AddRange([key, value]);
        }
        else
        {
            arguments[index + 1] = value;
        }
    }
}
