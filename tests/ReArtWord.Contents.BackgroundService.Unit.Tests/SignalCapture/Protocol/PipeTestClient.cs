using System.Globalization;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol.Tests;

/// <summary>発行済み AOT と開発 DLL に同じ pipe 契約を適用する試験クライアント。</summary>
internal sealed class PipeTestClient : IDisposable
{
    private readonly Process process;

    /// <summary>入力 pipe。</summary>
    internal Stream Input => process.StandardInput.BaseStream;

    /// <summary>出力 pipe。</summary>
    internal Stream Output => process.StandardOutput.BaseStream;

    /// <summary>開発 DLL または環境変数で指定された実行ファイルを起動します。</summary>
    internal PipeTestClient(bool demo = true, int? demoRate = null)
    {
        string? executable = Environment.GetEnvironmentVariable("CONTENTS_SIGNAL_CAPTURE_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = null;
        }
        string? dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = "dotnet";
        }
        var start = new ProcessStartInfo
        {
            FileName = executable ?? dotnetHost,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (executable is null)
        {
            start.ArgumentList.Add(typeof(WorkerSession).Assembly.Location);
        }
        start.ArgumentList.Add("--pipe");
        if (demo)
        {
            start.ArgumentList.Add("--demo");
            if (demoRate.HasValue)
            {
                start.ArgumentList.Add("--demo-rate");
                start.ArgumentList.Add(demoRate.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start worker.");
    }

    /// <summary>本体のフレーム生成処理を利用せず、契約どおりに制御を送信します。</summary>
    /// <returns>書き込み完了。</returns>
    internal async Task SendAsync(string json, CancellationToken cancellationToken = default)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 5];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length + 1);
        frame[4] = 1;
        payload.CopyTo(frame, 5);
        await Input.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await Input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本体と独立した長さ検証でフレームを読みます。</summary>
    /// <returns>kind と payload。</returns>
    internal async Task<PipeFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await Output.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > 2097152)
        {
            throw new InvalidDataException("Invalid worker output frame.");
        }
        var body = new byte[length];
        await Output.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new PipeFrame(body[0], body[1..]);
    }

    /// <summary>以前の定期通知を読み飛ばし、指定した稼働状態を確認します。</summary>
    /// <returns>指定した状態の通知。</returns>
    internal async Task<JsonElement> ReadStateAsync(bool running, CancellationToken cancellationToken = default)
    {
        JsonElement state;
        bool observedRunning;
        do
        {
            state = await ReadEventAsync("state", cancellationToken).ConfigureAwait(false);
            observedRunning = state.GetProperty("running").GetBoolean();
        }
        while (observedRunning != running);
        return state;
    }

    /// <summary>指定した制御応答まで読み進めます。</summary>
    /// <returns>独立して所有する JSON 値。</returns>
    internal async Task<JsonElement> ReadEventAsync(string type, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PipeFrame frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Kind != 1)
            {
                continue;
            }
            using JsonDocument json = JsonDocument.Parse(frame.Payload);
            string? receivedType = json.RootElement.GetProperty("type").GetString();
            if (string.Equals(receivedType, type, StringComparison.Ordinal))
            {
                return json.RootElement.Clone();
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>stdin EOF を送ります。</summary>
    internal void CloseInput() => process.StandardInput.Close();

    /// <summary>stdout の読み手を閉じます。</summary>
    internal void CloseOutput() => process.StandardOutput.Close();

    /// <summary>子プロセスの終了を待ちます。</summary>
    /// <returns>終了コード。</returns>
    internal async Task<int> ExitAsync(CancellationToken cancellationToken = default)
    {
        await ProcessCompletion.WaitAsync(process, cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>標準エラーを読み取ります。</summary>
    /// <returns>診断文字列。</returns>
    internal Task<string> ErrorAsync(CancellationToken cancellationToken = default) => process.StandardError.ReadToEndAsync(cancellationToken);

    /// <summary>試験で所有した子プロセスだけを回収します。</summary>
    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            process.WaitForExit();
        }
        finally
        {
            process.Dispose();
        }
    }
}
