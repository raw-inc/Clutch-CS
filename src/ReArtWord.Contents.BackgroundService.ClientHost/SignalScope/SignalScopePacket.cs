using System.Buffers.Binary;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>中継のメモリだけで所有し、ローリング時に解放する原本です。</summary>
internal sealed record SignalScopePacket(SignalScopeRow Row, string Details, byte[] Raw)
{
    /// <summary>原本・文字列・管理領域を含めた保持容量の概算です。</summary>
    internal long Size => Raw.Length + 2L * (Details.Length + Row.SourceId.Length + Row.ObservationPoint.Length + Row.Time.Length + Row.Source.Length
        + Row.Destination.Length + Row.Protocol.Length + Row.Transport.Length + Row.Summary.Length + Row.Fingerprint.Length) + 768;

    /// <summary>ローカル取得機構からのフレームを検証して所有します。</summary>
    /// <returns>外部へ未送信の原本と一覧情報です。</returns>
    internal static SignalScopePacket Read(ReadOnlySpan<byte> frame, long id)
    {
        if (frame.Length < 5 || frame[0] != 2)
        {
            throw new InvalidDataException("Invalid captured packet frame.");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(frame[1..]);
        if (length is < 2 or > 131072 || frame.Length < length + 5 || 9000 < frame.Length - length - 5)
        {
            throw new InvalidDataException("Invalid captured packet boundaries.");
        }
        Metadata metadata = JsonSerializer.Deserialize<Metadata>(frame.Slice(5, length), JsonOptions) ?? throw new InvalidDataException("Missing captured packet metadata.");
        string sourceId = Bounded(metadata.SourceId, 4096), observation = Bounded(metadata.ObservationPoint, 4096), time = Bounded(metadata.Time, 64);
        if (!DateTimeOffset.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _)
            || metadata.SourcePort is < 0 or > 65535 || metadata.DestinationPort is < 0 or > 65535)
        {
            throw new InvalidDataException("Invalid captured packet endpoint or time.");
        }
        string summary = Bounded(metadata.Summary, 512), value = Bounded(metadata.Value, 256);
        if (value.Length != 0)
        {
            summary += " · " + value;
        }
        SignalScopeRow row = new(id, sourceId, observation, time, metadata.LinkType, metadata.OriginalLength,
            Bounded(metadata.Source, 256), Bounded(metadata.Destination, 256), metadata.SourcePort, metadata.DestinationPort,
            Bounded(metadata.Protocol, 64), Bounded(metadata.Transport, 64), summary, Bounded(metadata.Fingerprint, 64));
        return new(row, Bounded(metadata.Details, 16384), frame[(length + 5)..].ToArray());
    }

    /// <summary>共有するJSON設定です。</summary>
    internal static JsonSerializerOptions JsonOptions
    {
        get;
    } = new(JsonSerializerDefaults.Web);

    /// <summary>本文や原本を含まない制御応答を作ります。</summary>
    /// <returns>WebSocket用のkind付き応答です。</returns>
    internal static byte[] Control<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] frame = new byte[json.Length + 1];
        frame[0] = 1;
        json.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>一覧は原本なし、選択した詳細だけを原本付きで符号化します。</summary>
    /// <returns>WebSocket用の長さ付きメタデータです。</returns>
    internal static byte[] Frame<T>(byte kind, T value, ReadOnlySpan<byte> raw = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] frame = new byte[json.Length + raw.Length + 5];
        frame[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), json.Length);
        json.CopyTo(frame, 5);
        raw.CopyTo(frame.AsSpan(json.Length + 5));
        return frame;
    }

    /// <summary>取得機構からの表示文字列にも上限を適用します。</summary>
    /// <returns>検証済みの文字列です。</returns>
    private static string Bounded(string? value, int maximum) => value is not null && value.Length <= maximum ? value
                                                                                                              : throw new InvalidDataException("Captured metadata exceeds its limit.");

    /// <summary>取得機構のJSONを一度だけ読み取ります。</summary>
    private sealed record Metadata(string SourceId, string ObservationPoint, string Time, int LinkType, int OriginalLength, string Source, string Destination,
        int? SourcePort, int? DestinationPort, string Protocol, string Transport, string Summary, string Details, string Fingerprint, string Value = "");
}
