using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Core停止後も一つの保守操作の結果だけを問い合わせます。Peerとしては参加しません。</summary>
public sealed class SetupResultChannel : IAsyncDisposable
{
    private readonly UdpTransport transport;
    private readonly string clusterId;
    private readonly Guid operationId;
    private readonly string senderId;
    private readonly Guid instanceId;

    /// <summary>操作固有の宛先と既存UDP経路を固定します。</summary>
    public SetupResultChannel(ClientNetworkBinding binding, string clusterId, Guid operationId, bool server)
    {
        ArgumentNullException.ThrowIfNull(binding);
        this.clusterId = clusterId;
        this.operationId = operationId;
        instanceId = Guid.NewGuid();
        senderId = "setup-observer:" + instanceId.ToString("N");
        if (server)
        {
            instanceId = operationId;
            senderId = "setup:" + operationId.ToString("N");
        }
        transport = new(new DatagramTransportOptions(senderId, binding.BindAddress, binding.BroadcastAddress, binding.Port) { RequestTimeout = InstallationDefaults.RequestTimeout }, instanceId, TimeProvider.System);
        transport.Initialize(binding.InterfaceId);
        transport.Start();
    }

    /// <summary>既に保存された結果だけを取得します。</summary>
    /// <returns>結果が未到達ならnullです。</returns>
    public async Task<SetupResult?> ReadAsync(CancellationToken cancellationToken = default)
    {
        WireMessage request = new(Guid.NewGuid(), clusterId, senderId, instanceId, "setup.result", "setup:" + operationId.ToString("N"), null, JsonSerializer.SerializeToElement(operationId), operationId);
        WireMessage? reply = await transport.RequestAsync(request, operationId, cancellationToken).ConfigureAwait(false);
        return reply?.Body.Deserialize<SetupResult>(ContractJson.Options);
    }

    /// <summary>実行の再受付をせず、固定操作の照会だけへ応答します。</summary>
    public void Serve(Func<SetupResult?> readResult)
    {
        ArgumentNullException.ThrowIfNull(readResult);
        transport.Received = async (message, _, token) =>
        {
            string kind = "setup.result";
            if (!string.Equals(message.Kind, kind, StringComparison.Ordinal) || !string.Equals(message.ClusterId, clusterId, StringComparison.Ordinal) || message.TargetInstanceId != operationId || message.Body.ValueKind != JsonValueKind.String || !message.Body.TryGetGuid(out Guid requested) || requested != operationId)
            {
                return;
            }
            SetupResult? result = readResult();
            if (result is not null)
            {
                await transport.SendAsync(new WireMessage(Guid.NewGuid(), clusterId, senderId, instanceId, kind, message.SenderId, message.Id, JsonSerializer.SerializeToElement(result, ContractJson.Options), message.InstanceId), token).ConfigureAwait(false);
            }
        };
    }

    /// <summary>保守結果専用socketを解放します。</summary>
    /// <returns>解放完了です。</returns>
    ValueTask IAsyncDisposable.DisposeAsync() => ((IAsyncDisposable)transport).DisposeAsync();
}
