using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

/// <summary>OS インターフェースとの関連付けを含む取得対象。</summary>
internal sealed record AdapterInfo(string Backend, string Id, string Name, string Details, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InterfaceId, string[] Addresses);
