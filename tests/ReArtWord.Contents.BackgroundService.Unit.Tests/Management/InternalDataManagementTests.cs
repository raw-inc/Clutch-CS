using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Management.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>公開内部データ操作と値を含まない状態照会を検証します。</summary>
public sealed class InternalDataManagementTests
{
    /// <summary>再接続の強いCASがCoreの明示未保存有効候補を暗黙破棄しません。</summary>
    /// <returns>保存版と有効版を混同しない拒否応答です。</returns>
    [Fact]
    public async Task StrongCompareExchangePreservesExistingUnsavedCandidate()
    {
        using MediaTestFiles files = new();
        AvailabilityOptions options = new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Runtime] = new(EAvailabilitySaveFailure.UseUnsaved) });
        ManagementTestPeer peer = Create(files.Root, options);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        ManagementRegistry registry = Registry(peer);
        InternalDataTarget target = new("workflow", EInternalDataScope.Peer, "key");
        InternalDataWriteResult initial = await CallAsync<InternalDataWriteResult>(registry, "internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), target, null, NodeValue.FromObject(42))).ConfigureAwait(true);
        string saved = initial.Result.Current!.Revision!;
        string dataset = AvailableNodeStateStore.DatasetId(NodeStateScope.Peer("a", "workflow"), "key");
        await peer.Runtime.Persistence.Availability.HandleSaveFailureAsync(EStoragePurpose.Runtime, dataset, NodeValue.FromObject(43), "unsaved", saved, Guid.NewGuid(), true, static (_, _) => Task.FromResult(EOutcome.Succeeded)).ConfigureAwait(true);
        InternalDataWriteResult rejected = await CallAsync<InternalDataWriteResult>(registry, "internal-data.compare-exchange", new InternalDataWriteRequest(Guid.NewGuid(), target, saved, NodeValue.FromObject(99))).ConfigureAwait(true);
        Assert.False(rejected.Result.Applied);
        Assert.Equal(EOutcome.NotExecuted, rejected.Result.PersistenceOutcome);
        Assert.Equal("Availability.CandidatePending", rejected.Result.Reason);
        Assert.True(rejected.Result.Current!.State.IsUnsaved);
        Assert.Equal(saved, rejected.Result.Current.State.SavedRevision);
        Assert.Equal("unsaved", rejected.Result.Current.State.EffectiveRevision);
        Assert.Equal(43, rejected.Result.Current.Value!.Json.GetInt32());
        InternalDataReadResult read = await CallAsync<InternalDataReadResult>(registry, "internal-data.read", target).ConfigureAwait(true);
        Assert.Equal(42, read.ConfirmedValue!.Json.GetInt32());
        Assert.Equal(43, read.Current.Value!.Json.GetInt32());
    }

    /// <summary>予約語をワークフロー名にしても内部knownを触らず、ローカル枝と確認値を区別します。</summary>
    /// <returns>実Coreサービスを通る保存範囲と比較条件の検証です。</returns>
    [Fact]
    public async Task PeerWorkflowNamedMembershipCannotOverwriteKnownMembers()
    {
        using MediaTestFiles files = new();
        ManagementTestPeer peer = Create(files.Root);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.Cluster.StartAsync().ConfigureAwait(true);
        DurableEntry? before = await peer.Repository.ReadAsync("peer:a:membership", "known").ConfigureAwait(true);
        Assert.NotNull(before);
        ManagementRegistry registry = Registry(peer);
        InternalDataTarget target = new("membership", EInternalDataScope.Peer, "known");
        InternalDataWriteResult written = await CallAsync<InternalDataWriteResult>(registry, "internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), target, null, NodeValue.FromObject(42))).ConfigureAwait(true);
        Assert.True(written.Result.Applied);
        Assert.Equal(before.Version, (await peer.Repository.ReadAsync("peer:a:membership", "known").ConfigureAwait(true))!.Version);
        Assert.Equal(42, (await peer.Repository.ReadAsync(NodeStateScope.Peer("a", "membership"), "known").ConfigureAwait(true))!.Value!.Json.GetInt32());
        InternalDataWriteResult conflict = await CallAsync<InternalDataWriteResult>(registry, "internal-data.compare-exchange", new InternalDataWriteRequest(Guid.NewGuid(), target, "wrong", NodeValue.FromObject(99))).ConfigureAwait(true);
        Assert.False(conflict.Result.Applied);
        InternalDataReadResult read = await CallAsync<InternalDataReadResult>(registry, "internal-data.read", target).ConfigureAwait(true);
        Assert.True(read.Current.State.CanRead);
        Assert.True(read.ConfirmedKnown);
        Assert.Equal(42, read.Current.Value!.Json.GetInt32());
        await peer.Cluster.DetachAsync().ConfigureAwait(true);
        InternalDataTarget shared = target with { Scope = EInternalDataScope.Shared };
        InternalDataWriteResult local = await CallAsync<InternalDataWriteResult>(registry, "internal-data.write", new InternalDataWriteRequest(Guid.NewGuid(), shared, null, NodeValue.FromObject(43))).ConfigureAwait(true);
        Assert.True(local.Result.Applied);
        Assert.True(local.Result.Current!.State.IsLocalUnconfirmed);
        InternalDataReadResult detached = await CallAsync<InternalDataReadResult>(registry, "internal-data.read", shared).ConfigureAwait(true);
        Assert.True(detached.Current.State.CanRead);
        Assert.False(detached.ConfirmedKnown);
        peer.Cluster.ReadinessCheck = _ => ValueTask.FromResult(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!peer.Cluster.GetPeers().Any(item => item.InstanceId == peer.Cluster.LocalInstanceId && item.Status == EPeerStatus.Unavailable))
        {
            await Task.Delay(25, timeout.Token).ConfigureAwait(true);
        }
        InternalDataReadResult unavailable = await CallAsync<InternalDataReadResult>(registry, "internal-data.read", target with { Key = "unobserved" }).ConfigureAwait(true);
        Assert.False(unavailable.Current.State.CanRead);
        Assert.False(unavailable.Current.State.CanWrite);
    }

    /// <summary>メタデータページに値を含めず、変更された集合のcursorを流用しません。</summary>
    /// <returns>件数を制限したページと秘密本文の非公開検証です。</returns>
    [Fact]
    public async Task AvailabilityPagesContainMetadataAndRejectChangedCursor()
    {
        using MediaTestFiles files = new();
        ManagementTestPeer peer = Create(files.Root);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        ManagementRegistry registry = Registry(peer);
        for (int index = 0; index < 3; index++)
        {
            await peer.Runtime.Persistence.Availability.RememberConfirmedAsync(EStoragePurpose.Runtime, "data-" + index, NodeValue.FromObject("secret-never-export-this"), "v1", true).ConfigureAwait(true);
        }
        AvailabilityListPage first = await CallAsync<AvailabilityListPage>(registry, "availability.list", new AvailabilityListQuery(2)).ConfigureAwait(true);
        Assert.Equal(2, first.Entries.Count);
        Assert.NotNull(first.Cursor);
        AvailabilityListPage last = await CallAsync<AvailabilityListPage>(registry, "availability.list", new AvailabilityListQuery(2, first.Cursor)).ConfigureAwait(true);
        Assert.Single(last.Entries);
        Assert.Null(last.Cursor);
        Assert.DoesNotContain("secret-never-export-this", JsonSerializer.Serialize(first, ContractJson.Options), StringComparison.Ordinal);
        await peer.Runtime.Persistence.Availability.RememberConfirmedAsync(EStoragePurpose.Runtime, "data-3", NodeValue.FromObject(4), "v1", true).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(registry, "availability.list", new AvailabilityListQuery(2, first.Cursor)).ConfigureAwait(true)).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await ExecuteAsync(registry, "availability.list", new AvailabilityListQuery(101)).ConfigureAwait(true)).Outcome);
    }

    /// <summary>対象専用の実Coreサービスを空きUDPportへ構成します。</summary>
    /// <returns>起動前の所有試験用の構成です。</returns>
    private static ManagementTestPeer Create(string root, AvailabilityOptions? availability = null)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        return new(root, Guid.NewGuid().ToString("N"), "a", port, true, availability: availability);
    }

    /// <summary>製品の実登録処理をそのまま使います。</summary>
    /// <returns>公開ドメイン操作のregistryです。</returns>
    private static ManagementRegistry Registry(ManagementTestPeer peer)
    {
        ManagementRegistry registry = new();
        InternalDataManagement.Register(registry, peer.Runtime);
        AvailabilityManagement.Register(registry, peer.Runtime);
        return registry;
    }

    /// <summary>実registryの応答を公開DTOとして検証します。</summary>
    /// <returns>正常なドメイン本文です。</returns>
    private static async Task<T> CallAsync<T>(ManagementRegistry registry, string operation, object payload)
    {
        ManagementReply reply = await ExecuteAsync(registry, operation, payload).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, reply.Outcome);
        return reply.Value!.Json.Deserialize<T>(ContractJson.Options)!;
    }

    /// <summary>輸送と独立した既存管理境界へ一件の公開要求を渡します。</summary>
    /// <returns>管理応答そのものです。</returns>
    private static Task<ManagementReply> ExecuteAsync(ManagementRegistry registry, string operation, object payload) => registry.ExecuteAsync(new(Guid.NewGuid(), "test", "a", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), operation, NodeValue.FromJson(JsonSerializer.SerializeToElement(payload, ContractJson.Options))));
}
