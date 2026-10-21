using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>Coreの寿命内で最大4件のリモートデスクトップを所有します。</summary>
internal sealed class DesktopService(CommonConfiguration configuration, SessionFailureBoundary failures) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Dictionary<Guid, DesktopSession> sessions = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private bool stopping;

    /// <summary>映像の開始だけを既存の管理受付へ登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Transient("desktop.open", async (value, token) => await OpenAsync(ManagementRegistry.Body<Guid>(value), token).ConfigureAwait(false));
        registry.Read("desktop.status", async (_, token) => await CountAsync(token).ConfigureAwait(false));
        registry.ReadValue("desktop.vbr", _ => true);
        registry.ReadValue("desktop.viewOnly", _ => true);
        registry.ReadValue("desktop.timing", _ => true);
        registry.ReadValue("desktop.clipboard", _ => OperatingSystem.IsWindows());
        registry.ReadValue("desktop.annotations", _ => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041));
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
    private async Task<DesktopTicket> OpenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || id == Guid.Empty)
        {
            throw new ManagementInputException("Remote desktop requires Windows and a session ID.");
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping)
            {
                throw new ManagementInputException("Core is stopping.");
            }
            if (sessions.TryGetValue(id, out DesktopSession? session))
            {
                return session.Ticket;
            }
            if (4 <= sessions.Count)
            {
                throw new ManagementInputException("All desktop sessions are in use.");
            }
            session = new(configuration.Cluster.BindAddress, configuration.FfmpegPath, failures, id);
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
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
                        DesktopSession session = sessions[id];
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
                foreach (DesktopSession session in sessions.Values)
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
