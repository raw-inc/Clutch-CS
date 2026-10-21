using System.Buffers.Binary;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;

/// <summary>画面転送の固定長ヘッダーと有限バッファの共通契約です。</summary>
public static class DesktopWire
{
    /// <summary>kind、monitor、長さ、flags、単調時刻を格納する長さです。</summary>
    public static int HeaderSize => 24;

    /// <summary>一画面の圧縮済みaccess unitの最大長です。</summary>
    public static int MaximumFrameBytes => 8 * 1024 * 1024;

    /// <summary>通常操作の固定長と、テキスト入力のヘッダー長です。</summary>
    public static int InputSize => 24;

    /// <summary>既存バッファへネットワーク順で書き込みます。kind=9のflagは画面参照喪失の通知です。</summary>
    public static void WriteHeader(Span<byte> bytes, int kind, int monitor, int length, bool keyFrame, long timestamp)
    {
        if (bytes.Length < HeaderSize || length < 0 || MaximumFrameBytes < length)
        {
            throw new ArgumentException("Invalid desktop frame length.", nameof(bytes));
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes, kind);
        BinaryPrimitives.WriteInt32BigEndian(bytes[4..], monitor);
        BinaryPrimitives.WriteInt32BigEndian(bytes[8..], length);
        int flags = 0;
        if (keyFrame)
        {
            flags = 1;
        }
        BinaryPrimitives.WriteInt32BigEndian(bytes[12..], flags);
        BinaryPrimitives.WriteInt64BigEndian(bytes[16..], timestamp);
    }

    /// <summary>種別・サイズ・画面範囲を検査します。kind=10はOpus、11はカーソル、13は時計照会、14は映像送信時刻、15はテキスト同期です。</summary>
    /// <returns>読み込むpayloadの長さです。</returns>
    public static int PayloadLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != HeaderSize)
        {
            throw new InvalidDataException("Invalid desktop header.");
        }
        int kind = BinaryPrimitives.ReadInt32BigEndian(bytes);
        int monitor = BinaryPrimitives.ReadInt32BigEndian(bytes[4..]);
        int length = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
        int flags = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
        int maximumFlags = 1;
        if (kind == 10)
        {
            maximumFlags = 3;
        }
        if (kind is < 1 or > 15 || monitor is < 0 or > 31 || length < 1 || MaximumFrameBytes < length || (kind == 9 && length != 8) || (kind == 10 && (length < 9 || 1283 < length))
            || (kind == 11 && (length != 4 || flags != 0))
            || (kind == 12 && (monitor != 0 || flags != 0))
            || (kind == 13 && (monitor != 0 || flags != 0 || length != 24))
            || (kind == 14 && (flags != 0 || length != 8))
            || (kind == DesktopClipboardWire.FrameKind && (monitor != 0 || flags != 0 || length < DesktopClipboardWire.PrefixSize || DesktopClipboardWire.MaximumTextBytes + DesktopClipboardWire.PrefixSize < length))
            || (kind != 2 && kind != DesktopClipboardWire.FrameKind && 65536 < length) || flags < 0 || maximumFlags < flags
            || BinaryPrimitives.ReadInt64BigEndian(bytes[16..]) < 0)
        {
            throw new InvalidDataException("Invalid desktop frame.");
        }
        return length;
    }
}
