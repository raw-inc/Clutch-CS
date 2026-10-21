using System.Buffers.Binary;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Nodes.Scripting;

/// <summary>継続スクリプトの一要素を、上限付きの長さ付きフレームで渡します。</summary>
internal static class ScriptStreamFrames
{
    /// <summary>本文サイズを確認してから長さと本文を順に書きます。</summary>
    /// <returns>一要素の受渡しです。受信側が遅いときはここで待ちます。</returns>
    internal static async Task WriteAsync<T>(Stream stream, T message, int maximum, CancellationToken cancellationToken = default)
    {
        using ScriptFrameBuffer buffer = new(maximum);
        await JsonSerializer.SerializeAsync(buffer, message, CSharpScriptWorker.ValueOptions, cancellationToken).ConfigureAwait(false);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, checked((int)buffer.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>宣言長を検証し、途中切断を正常完了として扱いません。</summary>
    /// <returns>所有する一フレームです。</returns>
    internal static async Task<JsonDocument> ReadAsync(Stream stream, int maximum, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || maximum < length)
        {
            throw new InvalidDataException("script.outputLimit");
        }
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }
}
