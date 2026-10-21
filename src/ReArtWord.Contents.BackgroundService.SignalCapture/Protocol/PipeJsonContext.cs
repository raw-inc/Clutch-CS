using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>NativeAOT で実行時リフレクションを使わない JSON 契約です。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeCommand))]
[JsonSerializable(typeof(PipeEvent))]
[JsonSerializable(typeof(PacketMetadata))]
internal sealed partial class PipeJsonContext : JsonSerializerContext
{
}
