using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Nodes.Media;

/// <summary>媒体の処理段階を付けて、明示された場合だけ実行診断を通知します。</summary>
internal static class MediaProcess
{
    /// <summary>通常の起動条件を維持し、診断指定時だけ段階を外部コマンドの情報へ付けます。</summary>
    /// <returns>正常終了したコマンドの標準出力です。</returns>
    internal static Task<string> RunAsync(MediaOptions options, string executable, IReadOnlyList<string> arguments, string stage, CancellationToken cancellationToken = default)
    {
        if (options.DiagnosticOutput is null)
        {
            return BoundedProcessRunner.RunAsync(executable, arguments, cancellationToken);
        }
        return BoundedProcessRunner.RunAsync(executable, arguments, new ProcessRunLimits(DiagnosticOutput: message => Write(options, stage, message)), cancellationToken);
    }

    /// <summary>利用者向けの一般メッセージへ変換する前に、元の例外を診断先へ残します。</summary>
    internal static void Failed(MediaOptions options, string stage, Exception error)
    {
        if (options.DiagnosticOutput is not null)
        {
            Write(options, stage, error.ToString());
        }
    }

    /// <summary>診断先の障害を媒体処理の結果へ波及させません。</summary>
    // 抑制理由: 任意の診断先へ通知する境界です。元の媒体処理の結果を維持します。
    [SuppressMessage("Design", "CA1031", Justification = "診断先の例外で媒体処理を失敗へ変更しません。")]
    private static void Write(MediaOptions options, string stage, string message)
    {
        try
        {
            options.DiagnosticOutput?.Invoke("[" + stage + "]\n" + message);
        }
        catch (Exception)
        {
            // 診断できなくても、元の成功・失敗・取消をそのまま返します。
        }
    }
}
