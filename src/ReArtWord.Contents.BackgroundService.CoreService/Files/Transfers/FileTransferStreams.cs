using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>ファイル転送の接続を有限数だけ所有し、Core停止時に全て回収します。</summary>
internal sealed class FileTransferStreams(CommonConfiguration configuration, PeerCluster cluster) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Dictionary<Guid, FileTransferSession> sessions = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool stopping;

    /// <summary>能力照会と接続許可だけを既存の管理受付へ登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.ReadValue("transfer.stream", _ => true);
        registry.Transient("transfer.stream.open", async (value, token) => await OpenAsync(registry, ManagementRegistry.Body<Guid>(value), token).ConfigureAwait(false));
    }

    /// <summary>元の接続IDへ一つの待受だけを確保します。</summary>
    /// <returns>短命で一回限りの許可です。</returns>
    // 抑制理由: sessionの所有権を辞書へ移動し、回収ループまたは停止で解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "sessionを辞書で所有し非同期回収します。")]
    private async Task<Contracts.Files.Transfers.FileTransferTicket> OpenAsync(ManagementRegistry registry, Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ManagementInputException("A file connection ID is required.");
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(id, out FileTransferSession? known))
            {
                return known.Ticket;
            }
            await ReapAsync(false, cancellationToken).ConfigureAwait(false);
            if (stopping || 8 <= sessions.Count)
            {
                throw new ManagementInputException("File transfer connections are unavailable.");
            }
            string address = configuration.Cluster.BindAddress;
            string interfaceId = configuration.Cluster.InterfaceId ?? ObserverNetwork.GetInterfaces()
                .Single(item => item.Addresses.Any(value => string.Equals(value.Address, address, StringComparison.Ordinal))).InterfaceId;
            FileTransferSession session = new(new LocalNetworkBinding(interfaceId, address),
                new(cluster.LocalClusterId, cluster.LocalPeerId, cluster.LocalInstanceId), registry);
            sessions.Add(id, session);
            return session.Ticket;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>完了接続と、停止時の全接続を回収します。</summary>
    /// <returns>Coreが所有する受付の寿命です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await changes.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await ReapAsync(false, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    changes.Release();
                }
            }
        }
        finally
        {
            await CloseSessionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>監視タスクの開始前に停止しても、所有する全接続を回収します。</summary>
    /// <returns>受付停止とセッションの解放完了です。</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // .NET 10では監視が始まらずfinallyも通らない停止があるため、ここでも回収します。
        await CloseSessionsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>通常終了と起動取消のどちらからも、所有セッションを一度だけ解放します。</summary>
    /// <returns>接続と受付の停止完了です。</returns>
    private async Task CloseSessionsAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stopping = true;
            await ReapAsync(true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>辞書から取り外した接続は完了を待って解放します。</summary>
    /// <returns>対象接続の回収完了です。</returns>
    // 抑制理由: 辞書から所有権を取り出し、このメソッド内で非同期解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "取り外した全sessionを非同期解放します。")]
    private async Task ReapAsync(bool all, CancellationToken cancellationToken = default)
    {
        foreach (Guid id in sessions.Where(item => all || item.Value.Completion.IsCompleted).Select(item => item.Key).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = sessions[id].ConfigureAwait(false);
            sessions.Remove(id);
        }
    }

    /// <summary>Host停止後に受付の同期資源を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }
}
