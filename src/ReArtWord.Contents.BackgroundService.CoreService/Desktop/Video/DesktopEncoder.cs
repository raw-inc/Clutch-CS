using System.Diagnostics;
using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Desktop.Capture;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop.Video;

/// <summary>有限待受のローカルRTPへ低遅延映像を出力する引数を構築します。</summary>
internal static class DesktopEncoder
{
    /// <summary>初期化を実際に試す順序です。開始後の自動再実行には使用しません。</summary>
    internal static IReadOnlyList<string> Candidates
    {
        get;
    } = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    /// <summary>VP9も実HW encodeを優先し、未対応時は有限threadのsoftwareへ移ります。</summary>
    internal static IReadOnlyList<string> Vp9Candidates
    {
        get;
    } = ["vp9_qsv", "libvpx-vp9"];

    /// <summary>HEVCのHW encodeを優先し、未対応時は低遅延x265を使用します。</summary>
    internal static IReadOnlyList<string> HevcCandidates
    {
        get;
    } = ["hevc_nvenc", "hevc_amf", "hevc_qsv", "libx265"];

    /// <summary>ユーザー設定の実行物をshellを介さず起動します。</summary>
    /// <returns>画面ごとに独立したエンコーダの起動設定です。</returns>
    internal static ProcessStartInfo StartInfo(string executable, DesktopMonitor monitor, string encoder, int port, bool probe = false, bool duplication = true, DesktopVideoSettings? settings = null)
    {
        settings ??= DesktopVideoSettings.FromWire(0, Vp9Candidates.Contains(encoder, StringComparer.Ordinal));
        settings.Validate();
        settings.CodecDetails?.Validate(Vp9Candidates.Contains(encoder, StringComparer.Ordinal));
        string fps = settings.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        int drawMouse = settings.HideCursor ? 0
                                            : 1;
        string rate = settings.MaximumBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";

        // VFR停止後のflushは次回に参照の再復号を必要とするため、
        // 既定は約250ms・最大16枚、詳細指定時も復号cacheの容量を守ります。
        int keyFrames = DesktopCodecOptions.KeyFrameLimit(settings);
        string gop = keyFrames.ToString(CultureInfo.InvariantCulture);
        string buffer = Math.Max(64, settings.MaximumBitrateKbps / 4).ToString(CultureInfo.InvariantCulture) + "k";
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-copyts", "-use_wallclock_as_timestamps", "1"];
        int? output = null;
        if (duplication && OperatingSystem.IsWindows())
        {
            output = DesktopDuplication.FindOutput(monitor.Name);
        }

        // mpdecimate skips the first eight columns in its block comparison.
        // Compare a padded image, then remove that border so edge-only cursor
        // changes are not mistaken for an unchanged desktop.
        string? filters = "pad=ceil(iw/2)*2+16:ceil(ih/2)*2+16:8:8,mpdecimate=hi=0:lo=0:frac=0,crop=iw-16:ih-16:8:8,format=yuv420p";
        int duplicateFrames = 0;
        if (settings.CodecDetails?.MaximumBFrames is > 0)
        {
            // B-frameは未来の参照を待つため、静止時も供給して最後の変化を排出します。
            duplicateFrames = 1;
            filters = "pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p";
        }
        if (output is int index)
        {
            // DDAの次のAcquireNextFrame待ちへ入る前にGPUから読み出します。
            // 別threadのencoder/filterへD3D11 surfaceを渡すと、疎な更新でも
            // GPUの受渡しが数百ms停滞します。-vf側のhwdownloadでは遅すぎます。
            arguments.AddRange(["-f", "lavfi", "-i", FormattableString.Invariant($"ddagrab=output_idx={index}:framerate={fps}:dup_frames={duplicateFrames}:draw_mouse={drawMouse},hwdownload,format=bgra")]);
            bool acceptsBgra = encoder switch { "h264_nvenc" or "h264_amf" or "hevc_nvenc" or "hevc_amf" => true, _ => false };
            if (acceptsBgra && monitor.Width % 2 == 0 && monitor.Height % 2 == 0)
            {
                // DDAの変更通知でVFRを保ち、色変換は対応encoderへ任せます。
                filters = null;
            }
        }
        else
        {
            arguments.AddRange(["-f", "gdigrab", "-framerate", fps, "-draw_mouse", drawMouse.ToString(CultureInfo.InvariantCulture),
                "-offset_x", monitor.Left.ToString(CultureInfo.InvariantCulture), "-offset_y", monitor.Top.ToString(CultureInfo.InvariantCulture),
                "-video_size", FormattableString.Invariant($"{monitor.Width}x{monitor.Height}"), "-i", "desktop"]);
        }
        arguments.Add("-an");
        if (filters is not null)
        {
            arguments.AddRange(["-vf", filters]);
        }
        arguments.AddRange(["-fps_mode", "vfr", "-c:v", encoder, "-g", gop]);

        // 到着した変更frameだけを独立参照にします。静止中のframeを生成せず、
        // 疎な更新で「120枚」が数分の履歴になることを防ぎます。
        arguments.AddRange(["-force_key_frames", "expr:if(isnan(prev_forced_t),1,gte(t-prev_forced_t,0.25))"]);
        if (!Vp9Candidates.Contains(encoder, StringComparer.Ordinal))
        {
            arguments.AddRange(["-bf", "0", "-refs", "1"]);
        }
        if (HevcCandidates.Contains(encoder, StringComparer.Ordinal))
        {
            arguments.AddRange(["-profile:v", "main"]);
        }
        switch (encoder)
        {
            case "hevc_nvenc":
            case "h264_nvenc": arguments.AddRange(["-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-delay", "0", "-rc-lookahead", "0", "-rc", "cbr", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer, "-forced-idr", "1"]); break;
            case "hevc_qsv": arguments.AddRange(["-preset", "veryfast", "-async_depth", "1", "-look_ahead_depth", "0", "-gpb", "0", "-forced_idr", "1", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]); break;
            case "h264_qsv": arguments.AddRange(["-preset", "veryfast", "-async_depth", "1", "-look_ahead", "0", "-forced_idr", "1", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]); break;
            case "hevc_amf":
            case "h264_amf": arguments.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-async_depth", "1", "-latency", "1", "-preanalysis", "0", "-forced_idr", "1", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]); break;
            case "libx265": arguments.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-forced-idr", "1", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer, "-x265-params", "repeat-headers=1:open-gop=0:scenecut=0:rc-lookahead=0:frame-threads=1:pools=4"]); break;
            case "libx264": arguments.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-forced-idr", "1", "-crf", "23", "-maxrate", rate, "-bufsize", buffer, "-x264-params", "repeat-headers=1:scenecut=0:rc-lookahead=0:sync-lookahead=0"]); break;
            case "vp9_qsv": arguments.AddRange(["-preset", "veryfast", "-async_depth", "1", "-profile:v", "profile0", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]); break;
            case "libvpx-vp9": arguments.AddRange(["-deadline", "realtime", "-cpu-used", "8", "-lag-in-frames", "0", "-auto-alt-ref", "0", "-row-mt", "1", "-tile-columns", "2", "-threads", "4", "-profile:v", "0", "-b:v", rate, "-maxrate", rate, "-bufsize", buffer]); break;
            default: throw new ArgumentException("Unsupported desktop encoder.", nameof(encoder));
        }
        DesktopCodecOptions.Apply(arguments, encoder, settings);
        if (probe)
        {
            arguments.AddRange(["-frames:v", "1", "-f", "null", "-"]);
        }
        else
        {
            arguments.AddRange(["-flush_packets", "1", "-start_time_realtime", "1", "-strict", "experimental", "-f", "rtp", "-payload_type", "96", FormattableString.Invariant($"rtp://127.0.0.1:{port}?pkt_size=1200&rtcpport={port}")]);
        }
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }
}
