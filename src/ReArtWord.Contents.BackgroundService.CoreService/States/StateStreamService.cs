using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

namespace ReArtWord.Contents.BackgroundService.CoreService.States;

/// <summary>Coreの寿命内で最大16件の状態表示接続を所有します。</summary>
internal sealed class StateStreamService(CommonConfiguration configuration, SessionFailureBoundary failures, ServiceRuntime runtime) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Dictionary<Guid, StateStreamSession> sessions = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool stopping;

    /// <summary>状態購読の開始だけを既存の管理受付へ登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Transient("state-stream.open", async (value, token) => await OpenAsync(ManagementRegistry.Body<Guid>(value), token).ConfigureAwait(false));
        registry.Read("state-stream.status", async (_, token) => await CountAsync(token).ConfigureAwait(false));
    }

    /// <summary>完了済みを含め、現在所有している接続数を返します。</summary>
    /// <returns>回収待ちも含むセッション数です。</returns>
    private async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return sessions.Count;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>同一開始IDは一つの期限付き接続だけを生成します。</summary>
    /// <returns>元のCore実体へ接続する一回限りのticketです。</returns>
    // 抑制理由: 辞書へ渡したsessionは回収ループまたはHost停止で解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "sessionの所有権を辞書へ移動します。")]
    private async Task<StateStreamTicket> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ManagementInputException("The state observation session ID is missing.");
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping)
            {
                throw new ManagementInputException("Core is stopping.");
            }
            if (sessions.TryGetValue(id, out StateStreamSession? session))
            {
                return session.Ticket;
            }
            if (16 <= sessions.Count)
            {
                throw new ManagementInputException("All state observation sessions are in use.");
            }
            session = new(configuration.Cluster.BindAddress, failures, id, runtime.State, configuration.Cluster.PeerId);
            sessions.Add(id, session);
            return session.Ticket;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>完了した接続とHost停止時の全接続を回収します。</summary>
    /// <returns>serviceの寿命です。</returns>
    // 抑制理由: 辞書が保持するsessionを取り外して非同期解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "辞書から所有資源を取り出して回収します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "辞書内の非同期sessionをすべて解放します。")]
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
                    foreach (Guid id in sessions.Where(item => item.Value.Completion.IsCompleted).Select(item => item.Key).ToArray())
                    {
                        StateStreamSession session = sessions[id];
                        sessions.Remove(id);
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
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
            foreach (StateStreamSession session in sessions.Values)
            {
                await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = session.ConfigureAwait(false);
            }
            sessions.Clear();
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>停止後の同期資源を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }
}
