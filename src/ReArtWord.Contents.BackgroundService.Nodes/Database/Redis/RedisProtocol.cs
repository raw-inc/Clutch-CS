using System.Globalization;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Redis;

/// <summary>対応操作だけが必要とするRESP2のスカラー応答を有限サイズで扱います。</summary>
internal static class RedisProtocol
{
    private static readonly UTF8Encoding utf8 = new(false, true);

    /// <summary>引数をbulk stringで区切り、CRLFを含む値もコマンドへ混入させません。</summary>
    /// <returns>送信完了です。</returns>
    internal static async Task WriteAsync(Stream stream, IReadOnlyList<byte[]> arguments, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes("*" + arguments.Count.ToString(CultureInfo.InvariantCulture) + "\r\n"), cancellationToken).ConfigureAwait(false);
        foreach (byte[] argument in arguments)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("$" + argument.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(argument, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>文字列・整数・nil・サーバーの明示拒否だけを読み取ります。</summary>
    /// <returns>エラーか、bulk値か、整数かを区別した結果です。</returns>
    internal static async Task<(bool Error, byte[]? Bytes, long? Number)> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken = default)
    {
        byte[] single = new byte[1];
        await stream.ReadExactlyAsync(single, cancellationToken).ConfigureAwait(false);
        byte prefix = single[0];
        string line = await LineAsync(stream, single, cancellationToken).ConfigureAwait(false);
        switch (prefix)
        {
            case (byte)'-':
                return (true, null, null);
            case (byte)':':
                return (false, null, long.Parse(line, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            case (byte)'+':
                return (false, utf8.GetBytes(line), null);
            case (byte)'$':
                int length = int.Parse(line, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (length == -1)
                {
                    return (false, null, null);
                }
                if (length < 0 || maximumBytes < length)
                {
                    throw new InvalidDataException("redis.responseLimit");
                }
                byte[] bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                byte[] end = new byte[2];
                await stream.ReadExactlyAsync(end, cancellationToken).ConfigureAwait(false);
                if (end[0] != 13 || end[1] != 10)
                {
                    throw new InvalidDataException("redis.responseMalformed");
                }
                return (false, bytes, null);
            default:
                throw new InvalidDataException("redis.responseUnsupported");
        }
    }

    /// <summary>bulk本文とは別に、RESP制御行を4KiB以内で確認します。</summary>
    /// <returns>CRLFを除いた制御行です。</returns>
    private static async Task<string> LineAsync(Stream stream, byte[] single, CancellationToken cancellationToken = default)
    {
        using MemoryStream line = new();
        for (int index = 0; index < 4096; index++)
        {
            await stream.ReadExactlyAsync(single, cancellationToken).ConfigureAwait(false);
            if (single[0] == 13)
            {
                byte[] tail = new byte[1];
                await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
                if (tail[0] != 10)
                {
                    throw new InvalidDataException("redis.responseMalformed");
                }
                return utf8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
            }
            line.WriteByte(single[0]);
        }
        throw new InvalidDataException("redis.controlLineLimit");
    }
}
