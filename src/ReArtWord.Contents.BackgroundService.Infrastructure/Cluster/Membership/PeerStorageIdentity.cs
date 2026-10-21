using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>保存資産を別クラスタや別Peerの新規状態として再利用させません。</summary>
internal sealed record PeerStorageIdentity(string ClusterId, string PeerId, string HostId, string InterfaceId, string Address)
{
    /// <summary>保存した通信割当を先に選び、輸送処理へ固定NICだけを渡します。</summary>
    /// <returns>所有先と通信割当の確認完了です。</returns>
    public static async Task InitializeAsync(IDurableRepository repository, ClusterOptions options, UdpTransport transport, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("peer:bootstrap", "identity", cancellationToken).ConfigureAwait(false);
        PeerStorageIdentity? saved = entry?.Value?.Json.Deserialize<PeerStorageIdentity>(ContractJson.Options);
        string? selected = options.InterfaceId ?? saved?.InterfaceId;
        if (selected is null)
        {
            IPAddress address = IPAddress.Parse(options.BindAddress);
            NetworkInterface[] candidates = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address))).ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidDataException("指定IPから初回NICを一意に確認できません。");
            }
            selected = candidates[0].Id;
        }
        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidDataException("NIC識別子を空にできません。");
        }
        await VerifyAsync(repository, options, selected, cancellationToken).ConfigureAwait(false);
        transport.Initialize(selected);
    }

    /// <summary>Peer名に依存しないローカル行を原子的に固定し、以降の起動で照合します。</summary>
    /// <returns>検証と必要な初回保存の完了。</returns>
    public static async Task VerifyAsync(IDurableRepository repository, ClusterOptions options, string interfaceId, CancellationToken cancellationToken = default)
    {
        string scope = "peer:bootstrap";
        string key = "identity";
        PeerStorageIdentity expected = new(options.Id, options.PeerId, options.HostId, interfaceId, options.BindAddress);
        DurableEntry? entry = await repository.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (entry is null && (await repository.ReadCurrentStateAsync(cancellationToken).ConfigureAwait(false)).Entries.Count != 0)
        {
            throw new InvalidDataException("所有identityのない既存共有資産を新規Clusterとして利用できません。");
        }
        if (entry is not null)
        {
            PeerStorageIdentity? actual = entry.Value?.Json.Deserialize<PeerStorageIdentity>(ContractJson.Options);
            if (actual is null || !string.Equals(actual.ClusterId, expected.ClusterId, StringComparison.Ordinal)
                || !string.Equals(actual.PeerId, expected.PeerId, StringComparison.Ordinal)
                || !string.Equals(actual.HostId, expected.HostId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存先のCluster・Peer・Host identityが起動設定と一致しません。");
            }
            if (actual == expected)
            {
                return;
            }
            if (options.InterfaceId is null)
            {
                throw new InvalidDataException("保存済みNICまたはIPと一致しません。人によるinterfaceIdの再選択が必要です。");
            }
        }
        DurableReceipt saved = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, key, entry?.Version, NodeValue.FromObject(expected))], cancellationToken).ConfigureAwait(false);
        if (saved.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidDataException("保存先identityを原子的に確認できません。");
        }
    }
}
