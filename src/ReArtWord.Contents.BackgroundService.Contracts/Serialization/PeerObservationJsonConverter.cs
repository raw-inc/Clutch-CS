using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Contracts.Serialization;

/// <summary>読取り応答だけに最終受信からの経過時間を添え、Peer間通知や永続記録の形式を変えません。</summary>
public sealed class PeerObservationJsonConverter(Func<TimeSpan, ObservationAge>? receive = null) : JsonConverter<PeerInfo>
{
    private static readonly JsonSerializerOptions compatible = new(ContractJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    private static string AgeProperty => "observationAgeTicks";

    /// <summary>閲覧側は通信にかかった時間も足して最終受信からの経過時間を復元します。通常の要求入力には適用しません。</summary>
    /// <returns>元の受信日時と、その日時に依存しない最終受信からの経過時間です。</returns>
    public override PeerInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement value = document.RootElement;
        PeerInfo peer = value.Deserialize<PeerInfo>(receive is null ? ContractJson.Options
                                                                    : compatible) ?? throw new JsonException("Peerの観測情報がありません。");
        if (receive is not null && value.TryGetProperty(AgeProperty, out JsonElement age))
        {
            if (age.ValueKind != JsonValueKind.Number || !age.TryGetInt64(out long ticks) || ticks < 0)
            {
                throw new JsonException("Peerの最終受信からの経過時間が不正です。");
            }
            peer = peer with { ObservationAge = receive(TimeSpan.FromTicks(ticks)) };
        }
        return peer;
    }

    /// <summary>この応答を書き出す時点の経過時間を付け、LastSeenの表示日時は維持します。</summary>
    public override void Write(Utf8JsonWriter writer, PeerInfo value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        JsonElement peer = JsonSerializer.SerializeToElement(value, ContractJson.Options);
        writer.WriteStartObject();
        foreach (JsonProperty property in peer.EnumerateObject())
        {
            property.WriteTo(writer);
        }
        if (value.ObservationAge is { } age)
        {
            writer.WriteNumber(AgeProperty, age.Elapsed.Ticks);
        }
        writer.WriteEndObject();
    }
}
