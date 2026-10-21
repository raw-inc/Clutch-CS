using System.Buffers.Binary;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Files;

/// <summary>ファイル操作だけを一つずつ運ぶ、長さ制限付きのTCPフレームです。</summary>
public static class FileTransferWire
{
    /// <summary>Base64と管理envelopeを含む一フレームの上限です。</summary>
    private static int MaximumBytes => 3 * 1024 * 1024;

    /// <summary>一件のJSONを送信し、途中失敗時は接続を再利用しません。</summary>
    /// <returns>一フレームの送信完了です。</returns>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (MaximumBytes < bytes.Length)
        {
            throw new InvalidDataException("File transfer frame exceeds its limit.");
        }
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>確保前に長さを検査し、切断したフレームを結果として返しません。</summary>
    /// <returns>全体を受信した一件です。</returns>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32BigEndian(header);
        if (count < 1 || MaximumBytes < count)
        {
            throw new InvalidDataException("Invalid file transfer frame length.");
        }
        byte[] bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, ContractJson.Options) ?? throw new InvalidDataException("Missing file transfer frame.");
    }

    /// <summary>永続確定や任意管理操作をこの経路から実行させません。</summary>
    /// <returns>この経路で配送する4操作かです。</returns>
    public static bool Allows(string operation) => operation is "transfer.stat" or "transfer.list" or "transfer.read" or "transfer.write";
}
