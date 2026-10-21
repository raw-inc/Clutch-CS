using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>要求・参照・操作照会の受信形式を、参加管理から分離して解釈します。</summary>
internal static class IncomingClusterRequests
{
    /// <summary>保存複製や一時通知とは異なる要求メッセージかを判定します。</summary>
    /// <returns>この境界で処理する種類かです。</returns>
    internal static bool Handles(string kind) => kind is "request" or "inspect" or "receipt.query";

    /// <summary>通常受付、読み取り専用参照、元結果の照会へ振り分けます。</summary>
    /// <returns>要求種類に対応する応答です。</returns>
    internal static async Task<JsonElement> ReceiveAsync(string kind, JsonElement body, RequestExecutor executor, Guid instance,
        Func<ClusterRequest, CancellationToken, Task<ClusterResponse>> execute, ClusterReplication replication, CancellationToken cancellationToken = default)
    {
        if (!Handles(kind))
        {
            return await replication.ReceiveAsync(kind, body, cancellationToken).ConfigureAwait(false);
        }
        switch (kind)
        {
            case "receipt.query":
                RequestQuery query = body.Deserialize<RequestQuery>() ?? throw new InvalidDataException("照会が不正です。");
                return JsonSerializer.SerializeToElement(await executor.ReadStatusAsync(query, cancellationToken).ConfigureAwait(false));
            case "inspect":
                ClusterRequest inspection = body.Deserialize<ClusterRequest>() ?? throw new InvalidDataException("要求が不正です。");
                return JsonSerializer.SerializeToElement(await executor.InspectAsync(inspection, instance, cancellationToken).ConfigureAwait(false));
            default:
                ClusterRequest request = body.Deserialize<ClusterRequest>() ?? throw new InvalidDataException("要求が不正です。");
                return JsonSerializer.SerializeToElement(await execute(request, cancellationToken).ConfigureAwait(false));
        }
    }
}
