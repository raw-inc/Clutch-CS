using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>実ffmpegを使用する小さな媒体試験用の構成を管理します。</summary>
internal sealed class MediaTestFiles : IDisposable
{
    private readonly ITestOutputHelper? testOutput;

    /// <summary>専用の一時rootと明示された実行物を用意します。</summary>
    internal MediaTestFiles(ITestOutputHelper? output = null, string? ffmpegPath = null, string? ffprobePath = null)
    {
        testOutput = output;
        Root = Path.Combine(Path.GetTempPath(), "contents-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string ffmpeg = ffmpegPath ?? Environment.GetEnvironmentVariable("CONTENTS_TEST_FFMPEG") ?? "C:/opt/ffmpeg/current/bin/ffmpeg.exe";
        string ffprobe = ffprobePath ?? Environment.GetEnvironmentVariable("CONTENTS_TEST_FFPROBE") ?? "C:/opt/ffmpeg/current/bin/ffprobe.exe";
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
    // 抑制理由: 生成失敗の診断を添え、元の例外はInnerExceptionに保持します。
    [SuppressMessage("SourceQuality", "RAW333", Justification = "呼出元に出力先がなくても、試験失敗から終了コードと標準エラーを確認できるようにします。")]
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
        using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 小さな固定媒体の生成が停止した場合も、試験用プロセスを回収します。
        generation.CancelAfter(TimeSpan.FromMinutes(1));
        string report = string.Empty;
        ProcessRunLimits limits = new(OutputCharacters: 16384)
        {
            DiagnosticOutput = value =>
            {
                report = value;
                testOutput?.WriteLine("[fixture-generation]\n" + value);
            },
        };
        try
        {
            _ = await BoundedProcessRunner.RunAsync(Options.FfmpegPath, arguments, limits, generation.Token).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("Fixture media generation failed.\n" + report, exception);
        }
        return path;
    }

    /// <summary>この試験用の構成が作った一時rootだけを削除します。</summary>
    public void Dispose()
    {
        Directory.Delete(Root, true);
    }
}
