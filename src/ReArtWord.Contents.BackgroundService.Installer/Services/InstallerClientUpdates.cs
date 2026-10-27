using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>選択したWebクライアントだけを、ローカルhelperまたは接続先Core経由で更新します。</summary>
internal static class InstallerClientUpdates
{
    /// <summary>このPCの配置を検証してから、独立Updaterへ引き継ぎます。</summary>
    /// <returns>起動確認を含む更新結果です。</returns>
    internal static async Task<SetupResult> LocalAsync(WebClientInstallation client, string payload, ClientNetworkBinding binding, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        WebClientUpdateInfo current = await WebClientUpdateProbe.ReadAsync(client, cancellationToken).ConfigureAwait(false);
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(payload, cancellationToken).ConfigureAwait(false);
        Guid operation = Guid.NewGuid();
        WebClientUpdateWorkspace workspace = new(client, new UpdatesOptions(InterfaceId: binding.InterfaceId, LocalAddress: binding.BindAddress,
            BroadcastAddress: binding.BroadcastAddress, ManagementPort: binding.Port, ClusterId: "installer"), "installer", Guid.NewGuid());
        WebClientUpdateJob job = await workspace.PrepareAsync(new(operation, client.InstallationId, manifest), current, cancellationToken).ConfigureAwait(false);
        progress?.Invoke(new("prepare-helper"));
        await ReleaseVerifier.CopyAsync(manifest, payload, job.PayloadRoot, cancellationToken).ConfigureAwait(false);
        string helper = await workspace.PrepareHelperAsync(operation, cancellationToken).ConfigureAwait(false);
        return await InstallerOperations.RunHelperAsync(operation, client.Root, helper, OperatingSystem.IsWindows() && client.RunAsService,
            "--update-web-client", progress, workspace.Layout(operation).OperationRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>固定したCoreの実体を通じて配布し、結果消失時には再実行しません。</summary>
    /// <returns>接続先の実結果または未確認状態です。</returns>
    internal static async Task<SetupResult> RemoteAsync(ObservedWebClient target, string payload, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObservedTarget core = target.Core ?? throw new InvalidDataException("Select the web client's Core service.");

        // Core内のHTTP稼働照会は最大2秒かかるため、その失敗応答を受け取る余裕も確保します。
        UpdateObserver observer = new(core.Binding, core.ClusterId, TimeSpan.FromSeconds(10));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        PeerInfo peer = await observer.FindAsync(core.PeerId, core.Peer?.InstanceId, cancellationToken).ConfigureAwait(false);
        WebClientUpdateInfo current = await SendAsync<WebClientUpdateInfo>(observer, peer, "setup.client.info", null, true, cancellationToken).ConfigureAwait(false);
        if (current.Installation.InstallationId != target.Installation.InstallationId)
        {
            throw new InvalidDataException("The web client installation changed. Search and select it again.");
        }
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(payload, cancellationToken).ConfigureAwait(false);
        await ReleaseVerifier.VerifyAsync(manifest, payload, cancellationToken).ConfigureAwait(false);
        Guid operation = Guid.NewGuid();
        progress?.Invoke(new("update-check"));
        await PrepareRemoteAsync(observer, peer, new(operation, target.Installation.InstallationId, manifest), cancellationToken).ConfigureAwait(false);
        UpdateStreamCapabilities capability = await SendAsync<UpdateStreamCapabilities>(observer, peer, "setup.client.stream.capabilities", null, true, cancellationToken).ConfigureAwait(false);
        UpdateStreamTicket ticket = await SendAsync<UpdateStreamTicket>(observer, peer, "setup.client.stream.open", operation, false, cancellationToken).ConfigureAwait(false);
        ClusterUpdateRequest transfer = new(operation, manifest, payload, [core.PeerId], EUpdateFailurePolicy.KeepCompatibleMixed);
        await UpdateStreamSender.SendAsync(transfer, core.Binding, peer.Address, capability.Port, ticket,
            (sent, total) => progress?.Invoke(new("update-transfer", CompletedBytes: sent, TotalBytes: total)), cancellationToken).ConfigureAwait(false);
        SetupResult accepted = await SendAsync<SetupResult>(observer, peer, "setup.client.start", operation, false, cancellationToken).ConfigureAwait(false);
        if (accepted.Finished)
        {
            return accepted;
        }
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(InstallationDefaults.RemoteTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ManagementReply reply = await observer.SendAsync(peer.PeerId, peer.InstanceId, "setup.client.result", UpdateObserver.Payload(operation), true, cancellationToken).ConfigureAwait(false);
            SetupResult? result = reply.Value?.Json.Deserialize<SetupResult>(ContractJson.Options);
            if (result is { Finished: true } && result.OperationId == operation)
            {
                return result;
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return new(operation, EOutcome.Unknown, "remote", "The final result could not be confirmed. Inspect the web client's update-staging folder on the target PC before retrying.", target.Installation.Root);
    }

    /// <summary>準備の操作IDだけを確認し、未対応のCoreに限って従来の準備要求へ進みます。</summary>
    /// <returns>同じ操作の準備確認です。結果不明では別の要求を送り直しません。</returns>
    internal static async Task PrepareRemoteAsync(UpdateObserver observer, PeerInfo peer, WebClientUpdateRequest request, CancellationToken cancellationToken = default)
    {
        string stage = "setup.client.stage";
        ManagementReply reply = await observer.SendAsync(peer.PeerId, peer.InstanceId, stage, UpdateObserver.Payload(request), false, cancellationToken).ConfigureAwait(false);
        string missing = "Management.OperationMissing";
        Guid prepared;
        if (reply.Outcome == EOutcome.NotExecuted && string.Equals(reply.Reason, missing, StringComparison.Ordinal))
        {
            WebClientUpdateJob legacy = await SendAsync<WebClientUpdateJob>(observer, peer, "setup.client.prepare", request, false, cancellationToken).ConfigureAwait(false);
            prepared = legacy.OperationId;
        }
        else
        {
            prepared = ReadReply<Guid>(reply, stage);
        }
        if (prepared != request.OperationId)
        {
            throw new InvalidDataException("The web client preparation response belongs to another operation.");
        }
    }

    /// <summary>未対応・拒否・結果不明を処理成功へ置き換えません。</summary>
    /// <returns>確認できた応答です。</returns>
    private static async Task<T> SendAsync<T>(UpdateObserver observer, PeerInfo peer, string operation, object? body, bool readOnly, CancellationToken cancellationToken = default)
    {
        Contracts.Nodes.NodeValue? payload = null;
        if (body is not null)
        {
            payload = UpdateObserver.Payload(body);
        }
        ManagementReply reply = await observer.SendAsync(peer.PeerId, peer.InstanceId, operation, payload, readOnly, cancellationToken).ConfigureAwait(false);
        return ReadReply<T>(reply, operation);
    }

    /// <summary>確認できた応答だけを、呼出し元が要求した型へ読み込みます。</summary>
    /// <returns>管理操作の応答です。</returns>
    private static T ReadReply<T>(ManagementReply reply, string operation)
    {
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            string missing = "Management.OperationMissing";
            if (string.Equals(reply.Reason, missing, StringComparison.Ordinal))
            {
                throw new IOException("The target Core does not support web client updates. Select its Core update as well, then update the web client.");
            }
            throw new IOException("The web client update request could not be confirmed (" + operation + "): " + reply.Reason);
        }
        return reply.Value.Json.Deserialize<T>(ContractJson.Options) ?? throw new InvalidDataException("The web client update response is missing.");
    }
}
