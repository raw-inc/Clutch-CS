using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

/// <summary>有限外部コマンドの終了確認と出力制限を担当します。</summary>
public static class BoundedProcessRunner
{
    private static readonly SemaphoreSlim windowsStarts = new(1, 1);

    /// <summary>既存コマンドの出力保持契約を維持します。</summary>
    /// <returns>上限付き標準出力です。</returns>
    public static Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => RunAsync(executable, arguments, new ProcessRunLimits(), cancellationToken);

    /// <summary>shellを使わず実行し、取消時にも実プロセスを停止します。</summary>
    /// <returns>正常終了時の上限付き標準出力です。</returns>
    public static Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, ProcessRunLimits limits, CancellationToken cancellationToken = default)
    {
        if (limits is { DiagnosticOutput: { } output })
        {
            return RunObservedAsync(executable, arguments, limits, new ProcessDiagnostics(executable, output), cancellationToken);
        }
        return RunCoreAsync(executable, arguments, limits, null, cancellationToken);
    }

    /// <summary>診断を指定した一回だけ、正常終了・起動失敗・取消を記録します。</summary>
    /// <returns>診断の有無で変わらない実行結果です。</returns>
    // 抑制理由: 元の例外を診断後にそのまま再送出します。
    [SuppressMessage("Design", "CA1031", Justification = "例外の種類を変換せず、明示した診断先へ記録します。"), SuppressMessage("SourceQuality", "RAW330", Justification = "診断のために元の例外とスタックを変更しません。")]
    private static async Task<string> RunObservedAsync(string executable, IReadOnlyList<string> arguments, ProcessRunLimits limits, ProcessDiagnostics diagnostics, CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            return await RunCoreAsync(executable, arguments, limits, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            diagnostics.Complete(failure);
        }
    }

    /// <summary>起動・入出力・停止を一回の所有範囲で処理します。</summary>
    /// <returns>正常終了時の上限付き標準出力です。</returns>
    // 抑制理由: 子プロセス停止を確認した後で取消を上位へ伝え、停止不能は結果不明へ分離します。
    [SuppressMessage("SourceQuality", "RAW330", Justification = "停止完了後も元の取消tokenを保持します。"), SuppressMessage("SourceQuality", "RAW333", Justification = "停止を確認できなかった場合だけ結果不明専用例外へ変換します。")]
    private static async Task<string> RunCoreAsync(string executable, IReadOnlyList<string> arguments, ProcessRunLimits limits, ProcessDiagnostics? diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.OutputCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.StandardInput?.Length ?? 0, 1048576);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("外部実行物が設定先にありません。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = limits.StandardInput is not null,
            WorkingDirectory = limits.WorkingDirectory ?? string.Empty,
        };
        if (limits.StandardInput is not null)
        {
            start.StandardInputEncoding = new UTF8Encoding(false);
        }
        if (limits.OutputEncoding is not null)
        {
            start.StandardOutputEncoding = limits.OutputEncoding;
            start.StandardErrorEncoding = limits.OutputEncoding;
        }
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (limits.EnvironmentOverrides is not null)
        {
            foreach (KeyValuePair<string, string?> item in limits.EnvironmentOverrides)
            {
                if (item.Value is null)
                {
                    start.Environment.Remove(item.Key);
                }
                else
                {
                    start.Environment[item.Key] = item.Value;
                }
            }
        }
        using Process process = new() { StartInfo = start };
        await StartAsync(process, cancellationToken).ConfigureAwait(false);
        diagnostics?.Started(process);
        using CancellationTokenSource pipes = new();
        using CancellationTokenSource resourceLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string> output = DrainAsync(process.StandardOutput, true, limits, resourceLimit.CancelAsync, diagnostics, pipes.Token);
        Task<string> errors = DrainAsync(process.StandardError, false, limits, resourceLimit.CancelAsync, diagnostics, pipes.Token);
        Task input = limits.StandardInput is null ? Task.CompletedTask
                                                  : FeedAsync(process.StandardInput, limits.StandardInput, resourceLimit.CancelAsync, pipes.Token);
        bool exceeded = false;
        try
        {
            Task exit = process.WaitForExitAsync(resourceLimit.Token);
            while (!exit.IsCompleted)
            {
                await Task.WhenAny(exit, Task.Delay(100, resourceLimit.Token)).ConfigureAwait(false);
                if (limits.WorkingSetBytes is long maximum && !process.HasExited)
                {
                    if (MemoryExceeded(process, maximum))
                    {
                        exceeded = true;
                        await resourceLimit.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            await exit.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                throw new ProcessStopUnconfirmedException();
            }
            throw;
        }
        finally
        {
            pipes.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await Task.WhenAll(output, errors, input).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pipes.IsCancellationRequested)
            {
                // 継承pipeを持つ子孫がいても、停止結果の通知を無期限に待たせません。
                if (diagnostics is not null)
                {
                    diagnostics.DrainTimedOut = true;
                }
            }
            finally
            {
                diagnostics?.Exited(process);
            }
        }
        if (exceeded || resourceLimit.IsCancellationRequested || process.ExitCode != 0)
        {
            throw new InvalidDataException("外部コマンドが異常終了しました。");
        }
        return await output.ConfigureAwait(false);
    }

    /// <summary>出力を並行して排出しながら入力を渡し、末尾でEOFを通知します。</summary>
    /// <returns>入力の受渡し、または途中切断によるプロセス停止要求です。</returns>
    private static async Task FeedAsync(StreamWriter writer, string content, Func<Task> failed, CancellationToken cancellationToken = default)
    {
        try
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await failed().ConfigureAwait(false);
        }
        finally
        {
            writer.Close();
        }
    }

    /// <summary>WindowsのCreateProcess内部の共通lockへ多数のThreadPoolスレッドを滞留させず、起動待ちを非同期化します。</summary>
    /// <returns>一回の起動確認です。起動済みプロセスの実行時間は直列化しません。</returns>
    private static async Task StartAsync(Process process, CancellationToken cancellationToken = default)
    {
        bool serialize = OperatingSystem.IsWindows();
        if (serialize)
        {
            await windowsStarts.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw new IOException("外部コマンドを開始できません。");
            }
        }
        finally
        {
            if (serialize)
            {
                windowsStarts.Release();
            }
        }
    }

    /// <summary>Windowsの同期pipeによるThreadPool占有を避け、文字コードとBOM判定を維持します。</summary>
    /// <returns>この一回の終了まで所有する出力readerです。</returns>
    private static StreamReader Reader(StreamReader original)
    {
        if (OperatingSystem.IsWindows() && original.BaseStream is FileStream file && !file.IsAsync)
        {
            return new StreamReader(new WindowsProcessPipe(file.SafeFileHandle), original.CurrentEncoding, detectEncodingFromByteOrderMarks: true);
        }
        return original;
    }

    /// <summary>終了競合は正常終了確認へ任せ、稼働中の観測不能は停止要求へ変換します。</summary>
    /// <returns>メモリー上限を維持できない場合はtrueです。</returns>
    private static bool MemoryExceeded(Process process, long maximum)
    {
        try
        {
            process.Refresh();
            return maximum < process.WorkingSet64;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return !process.HasExited;
        }
    }

    /// <summary>子プロセスのpipeを読み続け、ログへ生内容をそのまま出力しません。</summary>
    /// <returns>必要な標準出力だけを上限内で保持します。</returns>
    private static async Task<string> DrainAsync(StreamReader original, bool retain, ProcessRunLimits limits, Func<Task> limitReached, ProcessDiagnostics? diagnostics, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = Reader(original);
        StringBuilder result = new();
        char[] buffer = new char[4096];
        int count;
        long total = 0;
        try
        {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (!retain)
                {
                    diagnostics?.CaptureError(buffer.AsSpan(0, count));
                }
                if (limits.FailOnOutputLimit && limits.OutputCharacters < total)
                {
                    await limitReached().ConfigureAwait(false);
                }
                if (retain && result.Length < limits.OutputCharacters)
                {
                    result.Append(buffer, 0, Math.Min(count, limits.OutputCharacters - result.Length));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (diagnostics is not null)
            {
                diagnostics.DrainTimedOut = true;
            }

            // 本体終了後も子孫がpipeを保持する場合は、読取り期限までに受信した出力を返します。
            // 呼出元からの取消と終了コードはRunAsyncで別に判定します。
        }
        return result.ToString();
    }
}
