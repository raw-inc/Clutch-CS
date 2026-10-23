using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Media;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Media;

/// <summary>媒体処理の失敗段階と外部コマンドの情報が、試験出力へ残ることを確認します。</summary>
public sealed class MediaDiagnosticsTests(ITestOutputHelper testOutput)
{
    /// <summary>入力解析と変換の異常終了を区別し、終了コード・標準エラー・元の例外を残します。</summary>
    /// <returns>実ffmpeg・ffprobeで起こした失敗の診断です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessFailureReportsStageAndErrorAsync(bool conversion)
    {
        using MediaTestFiles files = new(testOutput);
        List<string> reports = [];
        MediaOptions options = Observe(files.Options, reports);
        string input = Path.Combine(files.Root, "invalid.wav");
        string stage = "input-probe";
        EOutcome expected = EOutcome.NotExecuted;
        if (conversion)
        {
            input = await files.CreateAsync("audio").ConfigureAwait(true);
            options = options with { FfmpegPath = options.FfprobePath };
            stage = "conversion";
            expected = EOutcome.Failed;
        }
        else
        {
            await File.WriteAllTextAsync(input, "not a media file").ConfigureAwait(true);
        }
        NodeResult result = await ConvertAsync(options, input, Path.Combine(files.Root, "output.wav")).ConfigureAwait(true);
        Assert.Equal(expected, result.Outcome);
        string process = Assert.Single(reports, report => report.StartsWith("[" + stage + "]", StringComparison.Ordinal) && report.Contains("exitCode=1", StringComparison.Ordinal));
        Assert.Contains("stderr:", process, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr:\n\nexception:", process, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), process, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(files.Root, "*.partial"));
    }

    /// <summary>外部コマンドが成功しても、要求と異なる形式の生成物は元の検証例外を残して拒否します。</summary>
    /// <returns>終了コード0と、出力形式の不一致を区別した診断です。</returns>
    [Fact]
    public async Task OutputValidationReportsOriginalExceptionAsync()
    {
        using MediaTestFiles files = new(testOutput);
        string wave = await files.CreateAsync("audio").ConfigureAwait(true);
        List<string> reports = [];
        MediaOptions options = Observe(files.Options, reports);
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaArtifact.InspectAsync(options, wave, Path.Combine(files.Root, "wrong.mp3"), "test", "audio/mpeg", "audio")).ConfigureAwait(true);
        Assert.Contains(reports, report => report.StartsWith("[output-probe]", StringComparison.Ordinal) && report.Contains("exitCode=0", StringComparison.Ordinal));
        Assert.Contains(reports, report => report.StartsWith("[output-validation]", StringComparison.Ordinal) && report.Contains(nameof(InvalidDataException), StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(files.Root, "wrong.mp3")));
    }

    /// <summary>診断先が失敗しても、生成物の検証と確定を完了します。</summary>
    /// <returns>診断を任意に追加しても正常な変換が失敗しないことです。</returns>
    [Fact]
    public async Task DiagnosticSinkFailureDoesNotPreventConversionAsync()
    {
        using MediaTestFiles files = new(testOutput);
        string input = await files.CreateAsync("audio").ConfigureAwait(true);
        MediaOptions options = files.Options with { DiagnosticOutput = _ => throw new InvalidOperationException("diagnostic sink failed") };
        NodeResult result = await ConvertAsync(options, input, Path.Combine(files.Root, "output.wav")).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
    }

    /// <summary>テスト結果へ診断を書き出し、同じ内容を検証にも使います。</summary>
    /// <returns>この一回だけの診断先を持つ設定です。</returns>
    private MediaOptions Observe(MediaOptions options, List<string> reports) => options with
    {
        DiagnosticOutput = message =>
        {
            reports.Add(message);
            testOutput.WriteLine(message);
        },
    };

    /// <summary>公開変換ノードを通してWAVを生成します。</summary>
    /// <returns>通常の失敗分類を保った実行結果です。</returns>
    private static ValueTask<NodeResult> ConvertAsync(MediaOptions options, string input, string output, CancellationToken cancellationToken = default)
    {
        IActionNode converter = (IActionNode)Assert.Single(MediaNodeRegistration.Create(options), node => string.Equals(node.Descriptor.Type, "media.audio.convert", StringComparison.Ordinal));
        return converter.ExecuteAsync(NodeTestContext.Create(JsonSerializer.Serialize(new { outputPath = output, format = "wav", sampleRate = 16000, channels = 1 }), NodeValue.FromObject(input)), cancellationToken);
    }
}
