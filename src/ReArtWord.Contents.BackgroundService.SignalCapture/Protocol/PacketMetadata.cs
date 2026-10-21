using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>原本とは独立した有界な表示情報。</summary>
internal sealed record PacketMetadata(string SourceId, string ObservationPoint, string Time, int LinkType, int OriginalLength, string Source, string Destination, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SourcePort, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DestinationPort, string Protocol, string Transport, string Summary, string Details, string Paths, string Value, string Fingerprint);
