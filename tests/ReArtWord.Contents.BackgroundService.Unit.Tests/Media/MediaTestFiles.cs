using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>実ffmpegを使用する小さな媒体試験用の構成を管理します。</summary>
internal sealed class MediaTestFiles : IDisposable
{
    private readonly ITestOutputHelper? testOutput;

    /// <summary>専用の一時rootと明示された実行物を用意します。</summary>
    internal MediaTestFiles(ITestOutputHelper? output = null)
    {
        testOutput = output;
        Root = Path.Combine(Path.GetTempPath(), "contents-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string ffmpeg = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFMPEG") ?? "C:/opt/ffmpeg/current/bin/ffmpeg.exe";
        string ffprobe = Environment.GetEnvironmentVariable("CONTENTS_TEST_FFPROBE") ?? "C:/opt/ffmpeg/current/bin/ffprobe.exe";
        Action<string>? diagnostic = null;
        if (output is not null)
        {
            diagnostic = output.WriteLine;
            output.WriteLine(FormattableString.Invariant($"[media-fixture] runtime={Environment.Version}; processors={Environment.ProcessorCount}"));
        }
        Options = new MediaOptions(ffmpeg, ffprobe, new FileSystemOptions([Root])) { DiagnosticOutput = diagnostic };
    }

    /// <summary>この試験用の構成だけの許可ルートです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>実ノードへ渡す許可範囲と実行物です。</summary>
    internal MediaOptions Options
    {
        get;
    }

    /// <summary>変換とHTTP 試験用の構成に使う実媒体を生成します。</summary>
    /// <returns>実在する入力パスです。</returns>
    internal async Task<string> CreateAsync(string kind, double durationSeconds = 0.3, CancellationToken cancellationToken = default)
    {
        Assert.True(File.Exists(Options.FfmpegPath), "Set CONTENTS_TEST_FFMPEG to the real ffmpeg executable.");
        Assert.True(File.Exists(Options.FfprobePath), "Set CONTENTS_TEST_FFPROBE to the real ffprobe executable.");
        string path = Path.Combine(Root, kind + ".mp4");
        string[] arguments = ["-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=10", "-t", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-pix_fmt", "yuv420p", path];
        string audioKind = "audio";
        string pictureKind = "picture";
        if (string.Equals(kind, audioKind, StringComparison.Ordinal))
        {
            path = Path.Combine(Root, "audio.wav");
            arguments = ["-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=16000", "-t", "0.2", "-c:a", "pcm_s16le", path];
        }
        if (string.Equals(kind, pictureKind, StringComparison.Ordinal))
        {
            path = Path.Combine(Root, "picture.png");
            arguments = ["-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i", "color=c=red:s=32x32", "-frames:v", "1", "-update", "1", path];
        }
        ProcessStartInfo start = new(Options.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        testOutput?.WriteLine(FormattableString.Invariant($"[fixture-generation]\nexecutable={Options.FfmpegPath}\nexitCode={process.ExitCode}\nstderr:\n{error[..Math.Min(error.Length, 16384)]}"));
        Assert.True(process.ExitCode == 0, error);
        return path;
    }

    /// <summary>この試験用の構成が作った一時rootだけを削除します。</summary>
    public void Dispose()
    {
        Directory.Delete(Root, true);
    }
}
