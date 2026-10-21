using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>配送と要求内容を分けた内部通信封筒です。</summary>
/// <param name="Id">配送ID。</param>
/// <param name="ClusterId">Cluster。</param>
/// <param name="SenderId">送信Peer。</param>
/// <param name="InstanceId">送信実体。</param>
/// <param name="Kind">通信種類。</param>
/// <param name="TargetId">明示宛先、全員ならnull。</param>
/// <param name="ReplyTo">応答先配送ID。</param>
/// <param name="Body">種類固有本文。</param>
/// <param name="TargetInstanceId">先頭の断片で照合する配送先実体。</param>
public sealed record WireMessage(Guid Id, string ClusterId, string SenderId, Guid InstanceId, string Kind, string? TargetId, Guid? ReplyTo, JsonElement Body,
    [property: JsonPropertyOrder(-1)] Guid? TargetInstanceId = null)
{
    /// <summary>同じ要求・実体・内容の応答を同じ配送にし、再送で欠けた断片を補えるようにします。</summary>
    /// <returns>要求を送った実体にだけ届く応答です。内容が変われば配送IDも変わります。</returns>
    public static WireMessage Reply(WireMessage request, string senderId, Guid instanceId, JsonElement body)
    {
        ArgumentNullException.ThrowIfNull(request);
        WireMessage response = new(Guid.Empty, request.ClusterId, senderId, instanceId, "reply", request.SenderId, request.Id, body, request.InstanceId);
        Guid deliveryId = new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(response)).AsSpan(0, 16));
        return response with { Id = deliveryId };
    }
}
