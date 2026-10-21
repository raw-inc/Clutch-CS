using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Display;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

namespace ReArtWord.Contents.BackgroundService.CoreService.DeviceManagement;

/// <summary>対話コンソールを要する表示操作と入力照会を一回だけ処理します。</summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleSettingsWorker
{
    /// <summary>親プロセスの一回限りのpipeで、固定された表示操作を受け付けます。</summary>
    /// <returns>一回の応答送信の完了です。</returns>
    internal static async Task RunAsync(string name, CancellationToken cancellationToken = default)
    {
        using NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000, cancellationToken).ConfigureAwait(false);
        Request request = await ReadAsync<Request>(pipe, cancellationToken).ConfigureAwait(false);
        if (request.ReadInput && request.Read.Area == EDeviceArea.Power && request.Change is null)
        {
            await WriteAsync(pipe, new Reply(Activity: WindowsIdleInput.CurrentSession()), cancellationToken).ConfigureAwait(false);
            return;
        }
        Reply reply;
        try
        {
            if (request.Read.Area != EDeviceArea.Display || request.Change is not null && request.Change.Action != EDeviceAction.Display)
            {
                throw new ArgumentException("Device.InvalidRequest");
            }
            WindowsDisplaySettings display = new();
            if (request.Change is null)
            {
                reply = new(await display.ReadAsync(request.Read, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                reply = new(Result: await display.ApplyAsync(request.Change, request.Before ?? throw new ArgumentException("Device.InvalidRequest"), cancellationToken).ConfigureAwait(false));
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            string prefix = "Device.";
            string problem = "Device.DisplayReadFailed";
            if (error.Message.StartsWith(prefix, StringComparison.Ordinal))
            {
                problem = error.Message;
            }
            reply = new(Problem: problem);
        }
        await WriteAsync(pipe, reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>長さの上限を持つJSONフレームを読みます。</summary>
    /// <returns>固定契約へ変換した要求または応答です。</returns>
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 2097152)
        {
            throw new InvalidDataException("Device.InvalidCommandOutput");
        }
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, ContractJson.Options) ?? throw new InvalidDataException("Device.InvalidCommandOutput");
    }

    /// <summary>外部文字列をコードやコマンド引数へ展開せずJSONで渡します。</summary>
    /// <returns>一つのフレームの送信完了です。</returns>
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (2097152 < body.Length)
        {
            throw new InvalidDataException("Device.InvalidRequest");
        }
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>表示領域だけに限定した要求です。</summary>
    internal sealed record Request(DeviceReadRequest Read, DeviceChangeRequest? Change = null, DeviceSnapshot? Before = null, bool ReadInput = false);

    /// <summary>実取得した設定、操作結果、または開始できない理由です。</summary>
    internal sealed record Reply(DeviceSnapshot? Snapshot = null, DeviceChangeResult? Result = null, string? Problem = null, DeviceUserActivity? Activity = null);
}
