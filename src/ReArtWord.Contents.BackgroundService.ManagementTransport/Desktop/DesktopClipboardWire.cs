using System.Buffers.Binary;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

/// <summary>テキスト同期のサイズ、要求識別子、反映確認を既存の入力列上で検証します。</summary>
public static class DesktopClipboardWire
{
    private static readonly UTF8Encoding encoding = new(false, true);

    /// <summary>一回のテキストをUTF-8で64KiBまでに制限します。</summary>
    public static int MaximumTextBytes => 65536;

    /// <summary>購読の有効・無効を切り替える固定長入力です。</summary>
    public static int SubscriptionKind => 15;

    /// <summary>24バイトの入力ヘッダーに長さ付き本文が続く書換え要求です。</summary>
    public static int WriteKind => 16;

    /// <summary>更新・反映確認・失敗を返す下りフレームです。</summary>
    public static int FrameKind => 15;

    /// <summary>種別、要求ID、OSの更新番号を置く本文接頭辞です。</summary>
    public static int PrefixSize => 12;

    /// <summary>既存入力は24バイトを維持し、テキストだけを明示された長さで受信します。</summary>
    /// <returns>ヘッダーを含む一入力の長さです。</returns>
    public static int InputLength(ReadOnlySpan<byte> header)
    {
        if (header.Length != DesktopWire.InputSize)
        {
            throw new InvalidDataException("Invalid clipboard input header.");
        }
        int kind = BinaryPrimitives.ReadInt32BigEndian(header);
        if (kind == SubscriptionKind)
        {
            if (BinaryPrimitives.ReadInt32BigEndian(header[4..]) is < 0 or > 1 || header[8..].ContainsAnyExcept((byte)0))
            {
                throw new InvalidDataException("Invalid clipboard subscription.");
            }
        }
        if (kind != WriteKind)
        {
            return DesktopWire.InputSize;
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
        if (BinaryPrimitives.ReadInt32BigEndian(header[4..]) < 1 || length < 0 || MaximumTextBytes < length || header[12..].ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("Invalid clipboard text request.");
        }
        return DesktopWire.InputSize + length;
    }

    /// <summary>不正UTF-8、NUL、上限超過を切り詰めず拒否します。</summary>
    /// <returns>元の改行とUnicodeを維持したテキストです。</returns>
    public static string ReadText(ReadOnlySpan<byte> bytes)
    {
        if (MaximumTextBytes < bytes.Length || bytes.Contains((byte)0) || !System.Text.Unicode.Utf8.IsValid(bytes))
        {
            throw new InvalidDataException("Invalid clipboard text.");
        }
        return encoding.GetString(bytes);
    }

    /// <summary>更新は0、反映確認は1、本文を含まないエラー通知は2として送ります。</summary>
    /// <returns>既存の有限フレーム中継へ渡す一件です。</returns>
    public static byte[] Frame(int mode, int requestId, uint sequence, string text = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (mode is < 0 or > 2 || requestId < 0 || mode == 0 && requestId != 0 || mode == 1 && (requestId == 0 || text.Length != 0)
            || text.Contains('\0', StringComparison.Ordinal) || MaximumTextBytes < encoding.GetByteCount(text))
        {
            throw new InvalidDataException("Invalid clipboard notification.");
        }
        byte[] frame = new byte[DesktopWire.HeaderSize + PrefixSize + encoding.GetByteCount(text)];
        DesktopWire.WriteHeader(frame, FrameKind, 0, frame.Length - DesktopWire.HeaderSize, false, 0);
        Span<byte> payload = frame.AsSpan(DesktopWire.HeaderSize);
        BinaryPrimitives.WriteInt32BigEndian(payload, mode);
        BinaryPrimitives.WriteInt32BigEndian(payload[4..], requestId);
        BinaryPrimitives.WriteUInt32BigEndian(payload[8..], sequence);
        _ = encoding.GetBytes(text, payload[PrefixSize..]);
        return frame;
    }
}
