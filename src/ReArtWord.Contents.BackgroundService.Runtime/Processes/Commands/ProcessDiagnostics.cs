using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

/// <summary>明示的に診断する一回のプロセスだけ、終了情報と有限の標準エラーを保持します。</summary>
internal sealed class ProcessDiagnostics(string executable, Action<string> output)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly StringBuilder errors = new();
    private int? processId;
    private int? exitCode;
    private bool truncated;

    /// <summary>本体終了後にパイプの読取りを打ち切ったかです。</summary>
    internal bool DrainTimedOut
    {
        get; set;
    }

    /// <summary>起動できた実プロセスの識別子を記録します。</summary>
    internal void Started(Process process) => processId = process.Id;

    /// <summary>停止を確認できた場合だけ、実際の終了コードを記録します。</summary>
    internal void Exited(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 診断のために元の停止結果を変更せず、終了コードは未取得として残します。
        }
    }

    /// <summary>標準エラーだけを上限まで保持し、以後の排出は継続します。</summary>
    internal void CaptureError(ReadOnlySpan<char> text)
    {
        int retained = Math.Min(text.Length, 16384 - errors.Length);
        errors.Append(text[..retained]);
        truncated |= retained < text.Length;
    }

    /// <summary>入力引数・環境変数・標準入力を含めず、呼出元が指定した診断先へ通知します。</summary>
    // 抑制理由: 診断先の障害によって、観測したコマンドの成功・失敗・取消を変えません。
    [SuppressMessage("Design", "CA1031", Justification = "任意の診断先の例外を、外部処理の結果へ波及させません。")]
    internal void Complete(Exception? failure)
    {
        try
        {
            string report = FormattableString.Invariant($"executable={executable}\npid={processId?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}\nexitCode={exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}\nelapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}\npipeDrainTimedOut={DrainTimedOut}\nstderrTruncated={truncated}\nstderr:\n{errors}");
            if (failure is not null)
            {
                report += "\nexception:\n" + failure;
            }
            output(report);
        }
        catch (Exception)
        {
            // 診断の成否と、外部処理の成否は別に扱います。
        }
    }
}
