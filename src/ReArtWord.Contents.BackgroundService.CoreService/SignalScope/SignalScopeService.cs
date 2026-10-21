using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.SignalScope;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

namespace ReArtWord.Contents.BackgroundService.CoreService.SignalScope;

/// <summary>Coreの寿命内で最大4件のキャプチャ接続を所有します。</summary>
internal sealed class SignalScopeService(CommonConfiguration configuration, SessionFailureBoundary failures) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Dictionary<Guid, SignalScopeSession> sessions = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool stopping;

    /// <summary>キャプチャの開始だけを既存の管理受付へ登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Transient("signal-scope.open", async (value, token) => await OpenAsync(ManagementRegistry.Body<Guid>(value), token).ConfigureAwait(false));
        registry.Read("signal-scope.status", async (_, token) => await CountAsync(token).ConfigureAwait(false));
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
    private async Task<SignalScopeTicket> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty || !File.Exists(SignalCaptureProcess.Executable))
        {
            throw new ManagementInputException("Capture worker is unavailable, or the session ID is missing.");
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping)
            {
                throw new ManagementInputException("Core is stopping.");
            }
            if (sessions.TryGetValue(id, out SignalScopeSession? session))
            {
                return session.Ticket;
            }
            if (4 <= sessions.Count)
            {
                throw new ManagementInputException("All capture sessions are in use.");
            }
            session = new(configuration.Cluster.BindAddress, failures, id);
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
                        SignalScopeSession session = sessions[id];
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
            await changes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                stopping = true;
                foreach (SignalScopeSession session in sessions.Values)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                sessions.Clear();
            }
            finally
            {
                changes.Release();
            }
        }
    }

    /// <summary>停止後の同期資源を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }
}
