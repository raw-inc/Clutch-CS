using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>画面のローカルHTTP要求を、明示したクラスタへのUDP管理操作に変換します。</summary>
internal static class ManagementFacade
{
    /// <summary>ローカルの画面用経路だけを一つのUDP操作へ対応させます。</summary>
    public static void MapRoute(WebApplication app, string method, string route, string operation, bool readOnly = false, bool body = false, bool transient = false, string? id = null)
    {
        app.MapMethods(route, [method], (HttpContext context, ObserverConnection connection, ClientCommandLedger ledger, CancellationToken cancellation) =>
            ForwardAsync(context, connection, ledger, operation, readOnly, body, transient, id, cancellation));
    }

    /// <summary>元の対象・期限・要求IDを確認し、応答不明を成功へ読み替えません。</summary>
    /// <returns>一回の操作または照会の結果です。</returns>
    private static async Task ForwardAsync(HttpContext context, ObserverConnection connection, ClientCommandLedger ledger, string operation, bool readOnly, bool body, bool transient, string? id, CancellationToken cancellationToken = default)
    {
        string clusterId = context.Request.Headers["X-Contents-Cluster"].ToString();
        string peerId = context.Request.Headers["X-Contents-Peer"].ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        UdpObserver observer = connection.FindObserver(clusterId, peerId) ?? throw new InvalidOperationException("選択したPeerへの通信経路を一意に確認できません。");
        PeerInfo[] peers = observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)).ToArray();
        if (peers.Length != 1)
        {
            throw new InvalidOperationException("選択したPeer実体を一意に確認できません。");
        }
        string instance = context.Request.Headers["X-Contents-Instance"].ToString();
        if (!string.IsNullOrEmpty(instance) && (!Guid.TryParse(instance, out Guid expected) || peers[0].InstanceId != expected))
        {
            throw new InvalidOperationException("選択したPeerが再起動しました。開始済みの要求を別実体へ送りません。");
        }
        NodeValue? payload = null;
        if (body)
        {
            JsonElement document = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            payload = NodeValue.FromJson(document);
        }
        if (id is not null)
        {
            payload = NodeValue.FromObject(context.Request.RouteValues[id]?.ToString() ?? throw new ArgumentException("対象IDがありません。", nameof(id)));
        }
        payload = RequestOrigin.Attach(operation, payload, context.Request.Headers["X-Contents-Display-Name"].FirstOrDefault());
        ManagementCommand command;
        if (readOnly || transient)
        {
            command = new(Guid.NewGuid(), clusterId, peerId, peers[0].InstanceId, DateTimeOffset.UtcNow.AddMinutes(1), operation, payload);
        }
        else
        {
            Guid sourceId = Guid.Parse(context.Request.Headers["X-Contents-Request"].ToString());
            if (sourceId == Guid.Empty)
            {
                throw new ArgumentException("送信前に固定した要求IDが必要です。", nameof(context));
            }
            Guid requestId = ClientCommandLedger.Identity(sourceId, operation);
            command = await ledger.PrepareAsync(requestId, clusterId, peerId, operation, payload, peers[0], cancellationToken).ConfigureAwait(false);
            context.Response.Headers["X-Contents-Command"] = requestId.ToString("D");
        }
        ManagementReply reply;
        if (readOnly)
        {
            reply = await observer.InspectAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (transient)
            {
                reply = await observer.NotifyAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                string fileCommit = "transfer.commit";
                if (reply.Outcome == EOutcome.Unknown && string.Equals(operation, fileCommit, StringComparison.Ordinal))
                {
                    reply = await PeerManagementClient.ConfirmFileCommitAsync(observer, command, reply, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        await RespondAsync(context, reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>管理輸送の成否とドメインの段階結果を分けて返します。</summary>
    /// <returns>画面の同一originへの応答完了です。</returns>
    private static async Task RespondAsync(HttpContext context, ManagementReply reply, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (reply.Outcome == EOutcome.Succeeded)
        {
            await context.Response.WriteAsJsonAsync(reply.Value?.Json, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            return;
        }
        int status = StatusCodes.Status409Conflict;
        string missing = "Management.ResourceMissing";
        if (string.Equals(reply.Reason, missing, StringComparison.Ordinal))
        {
            status = StatusCodes.Status404NotFound;
        }
        if (reply.Outcome == EOutcome.Unknown)
        {
            status = StatusCodes.Status504GatewayTimeout;
        }
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { reply.Outcome, reply.Reason, commandId = context.Response.Headers["X-Contents-Command"].ToString() }, ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }
}
