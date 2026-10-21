using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>同じ要求の再配送でJSONと分割バイト列を作り直さず、旧receiverの上限も守ります。</summary>
internal static class DatagramEncoding
{
    /// <summary>通常の管理要求はIPv4 MTU 1500内へ収め、大きな旧protocol配送は512分割以内を維持します。</summary>
    /// <returns>同じメッセージ IDとハッシュを持つ、再送可能な固定パケットです。</returns>
    internal static byte[][] Encode(WireMessage message, int maximumBytes)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (maximumBytes < body.Length)
        {
            throw new InvalidDataException("配送内容が上限を超えています。");
        }
        int chunkSize = 864;
        if (chunkSize * 512 < body.Length)
        {
            chunkSize = 12000;
        }
        int count = (body.Length + chunkSize - 1) / chunkSize;
        string hash = Convert.ToHexString(SHA256.HashData(body));
        byte[][] packets = new byte[count][];
        ArrayBufferWriter<byte> buffer = new(chunkSize * 2);
        using Utf8JsonWriter writer = new(buffer);
        for (int index = 0; index < count; index++)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(DatagramFragment.Id), message.Id);
            writer.WriteNumber(nameof(DatagramFragment.Index), index);
            writer.WriteNumber(nameof(DatagramFragment.Count), count);
            writer.WriteNumber(nameof(DatagramFragment.Length), body.Length);
            writer.WriteString(nameof(DatagramFragment.Hash), hash);
            int offset = index * chunkSize;
            writer.WriteBase64String(nameof(DatagramFragment.Payload), body.AsSpan(offset, Math.Min(chunkSize, body.Length - offset)));
            writer.WriteEndObject();
            writer.Flush();
            packets[index] = buffer.WrittenSpan.ToArray();
            buffer.ResetWrittenCount();
            writer.Reset(buffer);
        }
        return packets;
    }
}
