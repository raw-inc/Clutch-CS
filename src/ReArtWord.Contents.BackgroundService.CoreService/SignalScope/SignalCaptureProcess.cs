using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;

namespace ReArtWord.Contents.BackgroundService.CoreService.SignalScope;

/// <summary>キャプチャ専用プロセスと有界pipeを接続し、切断時は必ず終了させます。</summary>
internal static class SignalCaptureProcess
{
    /// <summary>配布資材内の実行ファイルだけを起動します。</summary>
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "tools", "signal-capture", "SignalCapture" + (OperatingSystem.IsWindows() ? ".exe"
                                                                                                                                                          : string.Empty));

    /// <summary>標準入出力だけを公開し、シェル・対話画面・外部の実行ファイル検索を使いません。</summary>
    /// <returns>通信とプロセスの終了です。</returns>
    // 抑制理由: 全通信Taskをfinallyで回収してから所有資源を解放します。
    [SuppressMessage("Reliability", "CA2025", Justification = "全方向のTaskをfinallyで取消し、完了を待ってからプロセスを解放します。")]
    internal static async Task RunAsync(Stream network, string address, int port, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo start = new(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--pipe");
        using Process process = Process.Start(start) ?? throw new IOException("Capture worker could not start.");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task output = process.StandardOutput.BaseStream.CopyToAsync(network, 65536, linked.Token);
        Task errors = DrainErrorsAsync(process.StandardError, linked.Token);
        Task input = CommandsAsync(network, process.StandardInput.BaseStream, address, port, linked.Token);
        try
        {
            await (await Task.WhenAny(input, output, errors).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            process.StandardInput.Close();
            try
            {
                using CancellationTokenSource grace = new(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await SessionRelay.DrainAsync(Task.WhenAll(input, output, errors), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>診断出力を保持せず排出し、stderr満杯でキャプチャを停止させません。</summary>
    /// <returns>プロセスの診断出力終了です。</returns>
    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken = default)
    {
        char[] buffer = new char[2048];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
        {
        }
    }

    /// <summary>キャプチャの制御だけを許可し、監視自身のTCP通信を除外します。</summary>
    /// <returns>ブラウザー側の接続終了です。</returns>
    private static async Task CommandsAsync(Stream network, Stream worker, string address, int port, CancellationToken cancellationToken = default)
    {
        string listKind = "list", startKind = "start", stopKind = "stop", exclusionsKind = "exclusions", sourcesName = "sources", excludedName = "excludedTcpEndpoints";
        byte[] buffer = new byte[65541];
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = await SignalScopeWire.ReadOrEndAsync(network, buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return;
            }
            if (buffer[4] != 1)
            {
                throw new InvalidDataException("Only capture commands are accepted.");
            }
            JsonObject value = JsonNode.Parse(buffer.AsSpan(5, length - 1)) as JsonObject ?? throw new InvalidDataException("Capture command is missing.");
            string? kind = value["type"]?.GetValue<string>();
            if (!string.Equals(kind, listKind, StringComparison.Ordinal) && !string.Equals(kind, startKind, StringComparison.Ordinal) && !string.Equals(kind, stopKind, StringComparison.Ordinal) && !string.Equals(kind, exclusionsKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unknown capture command.");
            }
            if (string.Equals(kind, startKind, StringComparison.Ordinal))
            {
                if (value[sourcesName] is not JsonArray sources || sources.Count is < 1 or > 16)
                {
                    throw new InvalidDataException("Select between 1 and 16 capture sources.");
                }
            }
            if (string.Equals(kind, startKind, StringComparison.Ordinal) || string.Equals(kind, exclusionsKind, StringComparison.Ordinal))
            {
                JsonArray excluded = value[excludedName] as JsonArray ?? new();
                if (128 < excluded.Count)
                {
                    throw new InvalidDataException("Too many capture exclusions.");
                }
                excluded.Add(new JsonObject { ["address"] = address, ["port"] = port });
                if (value[excludedName] is null)
                {
                    value[excludedName] = excluded;
                }
            }
            await worker.WriteAsync(SignalScopeWire.Control(JsonSerializer.SerializeToElement(value)), cancellationToken).ConfigureAwait(false);
            await worker.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
