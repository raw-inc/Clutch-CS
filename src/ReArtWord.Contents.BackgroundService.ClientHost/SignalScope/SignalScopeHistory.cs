using System.Net;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>一つの閲覧接続の原本を、件数と容量でローリングします。永続化しません。</summary>
internal sealed class SignalScopeHistory
{
    private readonly Lock sync = new();
    private readonly Dictionary<long, SignalScopePacket> packets = [];
    private readonly Queue<long> order = new();
    private readonly HashSet<string> exclusions = new(StringComparer.Ordinal);
    private int maximumCount = 50000;
    private long maximumBytes = 320 * 1024 * 1024;
    private long bytes;
    private long nextId;
    private long generation;
    private long queryId;
    private SignalScopeQuery query = new(null, null);
    private (string HostName, IReadOnlyDictionary<string, string> Names) display = (string.Empty, new Dictionary<string, string>());

    /// <summary>原本の保持量を一覧用メモリとは区別して通知します。</summary>
    internal (int Count, long Bytes) Usage
    {
        get
        {
            using (sync.EnterScope())
            {
                return (packets.Count, bytes);
            }
        }
    }

    /// <summary>原本を保持し、通常は一覧列だけを返します。</summary>
    /// <returns>原本なしのkind 2、または除外対象ならnullです。</returns>
    internal byte[]? Add(ReadOnlySpan<byte> frame)
    {
        SignalScopePacket packet = SignalScopePacket.Read(frame, Interlocked.Increment(ref nextId));
        SignalScopeSearchSnapshot snapshot;
        using (sync.EnterScope())
        {
            if (Excluded(packet))
            {
                return null;
            }
            packets.Add(packet.Row.PacketId, packet);
            order.Enqueue(packet.Row.PacketId);
            bytes += packet.Size;
            Trim();
            snapshot = Snapshot();
        }
        bool matched = snapshot.Query.Matches(packet, snapshot.HostName, snapshot.Names);
        return SignalScopePacket.Frame(2, packet.Row with { QueryId = snapshot.RequestId, Matched = matched });
    }

    /// <summary>全設定を検証してから、同じ保持領域へ反映します。</summary>
    internal void Configure(SignalScopeRequest request)
    {
        if (request.MaximumCount is < 1 or > 250000 || request.MaximumBytes is < 65536 or > 1073741824 || 256 < request.HostName?.Length)
        {
            throw new ArgumentException("Capture history limits are out of range.");
        }
        IReadOnlyDictionary<string, string>? configuredNames = request.Names is null ? null
                                                                                     : SignalScopeQuery.Names(request.Names);
        using (sync.EnterScope())
        {
            maximumCount = request.MaximumCount ?? maximumCount;
            maximumBytes = request.MaximumBytes ?? maximumBytes;
            display = (request.HostName ?? display.HostName, configuredNames ?? display.Names);
            Trim();
        }
    }

    /// <summary>開始時点のID範囲だけを固定し、原本全件への参照を複製しません。</summary>
    /// <returns>検索に必要な有限の設定です。</returns>
    internal SignalScopeSearchSnapshot BeginSearch(SignalScopeRequest request)
    {
        if (request.RequestId < 1)
        {
            throw new ArgumentException("Missing advanced search identity.");
        }
        SignalScopeQuery replacement = new(request.Expression, request.Mode);
        using (sync.EnterScope())
        {
            query = replacement;
            queryId = request.RequestId;
            return Snapshot();
        }
    }

    /// <summary>原本の保持参照は一回最大256件だけ貸し出します。</summary>
    /// <returns>既にローリングされた原本を含まないバッチです。</returns>
    internal SignalScopePacket[] Batch(SignalScopeSearchSnapshot snapshot, ref long position)
    {
        using (sync.EnterScope())
        {
            if (snapshot.Generation != generation || snapshot.RequestId != queryId || packets.Count == 0)
            {
                position = snapshot.LastId;
                return [];
            }
            List<SignalScopePacket> result = new(256);
            int examined = 0;
            while (position < snapshot.LastId && result.Count < 256 && examined++ < 4096)
            {
                if (packets.TryGetValue(++position, out SignalScopePacket? packet))
                {
                    result.Add(packet);
                }
            }
            return result.ToArray();
        }
    }

    /// <summary>選択された原本だけを、現在の除外条件で確認します。</summary>
    /// <returns>存在しなければ期限切れの応答です。</returns>
    internal byte[] Detail(SignalScopeRequest request)
    {
        using (sync.EnterScope())
        {
            if (request.PacketId < 1 || request.RequestId < 1)
            {
                throw new ArgumentException("Missing captured packet identity.");
            }
            if (!packets.TryGetValue(request.PacketId, out SignalScopePacket? packet) || Excluded(packet))
            {
                return SignalScopePacket.Control(new { type = "detail", request.PacketId, request.RequestId, removed = true });
            }
            SignalScopeRow row = packet.Row;
            return SignalScopePacket.Frame(3, new { row.PacketId, request.RequestId, row.SourceId, row.ObservationPoint, row.Time, row.LinkType, row.OriginalLength,
                row.Source, row.Destination, row.SourcePort, row.DestinationPort, row.Protocol, row.Transport, row.Summary, row.Fingerprint, packet.Details }, packet.Raw);
        }
    }

    /// <summary>取得自身の除外先が変わったら、該当する過去原本も破棄します。</summary>
    internal void Exclude(JsonElement command)
    {
        HashSet<string> replacement = new(StringComparer.Ordinal);
        foreach (string property in new[] { "excludedTcpEndpoints", "excludedEndpoints" })
        {
            if (!command.TryGetProperty(property, out JsonElement values))
            {
                continue;
            }
            string tcp = "tcp", udp = "udp", transportName = "transport";
            foreach (JsonElement value in values.EnumerateArray())
            {
                string transport = value.TryGetProperty(transportName, out JsonElement transportValue) ? transportValue.GetString() ?? string.Empty
                                                                                                       : "tcp";
                string? address = value.GetProperty("address").GetString();
                int port = value.GetProperty("port").GetInt32();
                if (256 <= replacement.Count || (!string.Equals(transport, tcp, StringComparison.Ordinal) && !string.Equals(transport, udp, StringComparison.Ordinal)) || !IPAddress.TryParse(address, out IPAddress? parsed) || port is < 1 or > 65535)
                {
                    throw new ArgumentException("Invalid capture exclusion.");
                }
                replacement.Add(Endpoint(transport, parsed.ToString(), port));
            }
        }
        using (sync.EnterScope())
        {
            exclusions.Clear();
            exclusions.UnionWith(replacement);
            foreach (SignalScopePacket packet in packets.Values.Where(Excluded).ToArray())
            {
                packets.Remove(packet.Row.PacketId);
                bytes -= packet.Size;
            }
            Trim();
        }
    }

    /// <summary>画面離脱・明示クリアで原本と解析結果への参照をすべて破棄します。</summary>
    internal void Clear()
    {
        using (sync.EnterScope())
        {
            packets.Clear();
            order.Clear();
            bytes = 0;
            generation++;
        }
    }

    /// <summary>排他中の設定とID範囲を固定します。</summary>
    /// <returns>原本を持たない検索のスナップショットです。</returns>
    private SignalScopeSearchSnapshot Snapshot() => new(queryId, generation, order.TryPeek(out long first) ? first
                                                                                                           : nextId + 1, nextId, query, display.HostName, display.Names);

    /// <summary>先頭除去はO(1)とし、除外条件の変更でできた穴も圧縮します。</summary>
    private void Trim()
    {
        while (order.TryPeek(out long first) && (!packets.ContainsKey(first) || maximumCount < packets.Count || maximumBytes < bytes))
        {
            order.Dequeue();
            if (packets.Remove(first, out SignalScopePacket? packet))
            {
                bytes -= packet.Size;
            }
        }
        if (packets.Count * 2 + 2048 < order.Count)
        {
            long[] retained = order.Where(packets.ContainsKey).ToArray();
            order.Clear();
            foreach (long id in retained)
            {
                order.Enqueue(id);
            }
        }
    }

    /// <summary>取得済みメタデータでTCP/UDPの除外端点を照合します。</summary>
    /// <returns>除外された原本かです。</returns>
    private bool Excluded(SignalScopePacket packet)
    {
        SignalScopeRow row = packet.Row;
        return exclusions.Contains(Endpoint(row.Transport, row.Source, row.SourcePort)) || exclusions.Contains(Endpoint(row.Transport, row.Destination, row.DestinationPort));
    }

    /// <summary>同じ端点を表す固定の比較キーです。</summary>
    /// <returns>通信方式・IP・portの組です。</returns>
    private static string Endpoint(string transport, string address, int? port) => transport.ToUpperInvariant() + "/" + address + "/" + port?.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
