using System.Diagnostics.CodeAnalysis;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>ピアの共有PTYセッションを上限付きで所有します。</summary>
internal sealed class TerminalService(ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading.CommonConfiguration configuration,
    ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.IDurableRepository repository, SessionFailureBoundary failures) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly TerminalSettingsStore settings = new(repository, configuration.Cluster.PeerId);
    private readonly Dictionary<Guid, TerminalSession> sessions = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly TerminalShellCatalog shellCatalog = new(TerminalShellDiscovery.Discover());
    private bool stopping;

    /// <summary>作成・引継ぎ・終了は永続受付、入力は連番付き一時操作として登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Read("terminal.list", async (_, token) => await ListAsync(token).ConfigureAwait(false));
        registry.Read("terminal.shells", (_, _) => Task.FromResult<object?>(shellCatalog.Shells));
        registry.Read("terminal.capabilities", async (_, token) =>
        {
            TerminalSettings current = await settings.ReadAsync(token).ConfigureAwait(false);
            TerminalCapabilities available = TerminalIdentity.Capabilities(current.StrictUserToken);
            if (OperatingSystem.IsWindows())
            {
                available = available with { Settings = current };
            }
            return available;
        });
        registry.Write("terminal.settings.save", async (value, token) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Windows token settings are not available on this Core.");
            }
            return await settings.SaveAsync(ManagementRegistry.Body<TerminalSettings>(value), token).ConfigureAwait(false);
        });
        registry.Transient("terminal.attach", async (value, token) => await AttachAsync(ManagementRegistry.Body<TerminalAttachRequest>(value), token).ConfigureAwait(false));
        registry.Write("terminal.create", async (value, token) => await CreateAsync(ManagementRegistry.Body<TerminalRequest>(value), token).ConfigureAwait(false));
        registry.Write("terminal.claim", (value, token) => OperateAsync("claim", ManagementRegistry.Body<TerminalRequest>(value), token));
        registry.Write("terminal.close", (value, token) => OperateAsync("close", ManagementRegistry.Body<TerminalRequest>(value), token));
        registry.Read("terminal.read", (value, token) => OperateAsync("read", ManagementRegistry.Body<TerminalRequest>(value), token));
        registry.Transient("terminal.input", (value, token) => OperateAsync("input", ManagementRegistry.Body<TerminalRequest>(value), token));
    }

    /// <summary>共有PTYを維持したまま、その所有者の表示接続を開きます。</summary>
    /// <returns>一回限りの接続許可です。</returns>
    private async Task<TerminalTicket> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await sessions[request.SessionId].AttachAsync(configuration.Cluster.BindAddress, request, cancellationToken).ConfigureAwait(false);
        }

        finally
        {
            changes.Release();
        }
    }

    /// <summary>同じ作成IDは二つのシェルを起動しません。</summary>
    /// <returns>作成した端末です。</returns>
    // 抑制理由: 作成したPTYとセッションの所有権を辞書へ渡し、終了時とHost停止時に回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "セッションの所有権を辞書へ移動します。")]
    internal async Task<TerminalView> CreateAsync(TerminalRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty || request.Owner == Guid.Empty)
        {
            throw new ArgumentException("Session and owner are required.", nameof(request));
        }

        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopping)
            {
                throw new ManagementInputException("Core is stopping.");
            }
            string shell = shellCatalog.Resolve(request.Shell);
            if (sessions.TryGetValue(request.SessionId, out TerminalSession? existing))
            {
                if (!string.Equals(existing.View.Shell, shell, StringComparison.Ordinal) || existing.View.Elevated != request.Elevated)
                {
                    throw new ArgumentException("The session ID already belongs to a different shell or privilege.", nameof(request));
                }
                return existing.View;
            }

            if (16 <= sessions.Count)
            {
                throw new InvalidOperationException("Close an existing terminal first (maximum 16).");
            }

            string directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(directory))
            {
                directory = AppContext.BaseDirectory;
            }

            PtyOptions options = new()
            {
                App = shell,
                Cwd = directory,
                Cols = 100,
                Rows = 30,
                Name = "xterm-256color",
                CommandLine = [],
            }
;
            TerminalSettings current = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            (IPtyConnection process, bool system) = await TerminalStartup.SpawnAsync(options, request.Elevated, current.StrictUserToken, cancellationToken).ConfigureAwait(false);
            TerminalSession session = new(process, request.SessionId, shell, request.Owner, failures, request.Elevated, system);
            sessions.Add(request.SessionId, session);
            return session.View;
        }

        finally
        {
            changes.Release();
        }
    }

    /// <summary>共有セッション一覧を不変の値で返します。</summary>
    /// <returns>最大16件の状態です。</returns>
    internal async Task<TerminalView[]> ListAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return sessions.Values.Select(session => session.View).ToArray();
        }

        finally
        {
            changes.Release();
        }
    }

    /// <summary>削除とI/Oが競合しないよう所有寿命を維持します。</summary>
    /// <returns>操作の確認結果です。</returns>
    // 抑制理由: 辞書が所有するセッションを借用し、明示終了の場合だけ取り外して解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "辞書の所有資源を操作の間だけ借用します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "辞書から取り外したセッションを明示終了します。")]
    internal async Task<object?> OperateAsync(string operation, TerminalRequest request, CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TerminalSession session = sessions[request.SessionId];
            switch (operation)
            {
                case "read": return session.Read(request.Offset);
                case "input": return await session.InputAsync(request, cancellationToken).ConfigureAwait(false);
                case "claim": return await session.ClaimAsync(request.Owner, cancellationToken).ConfigureAwait(false);
                case "close":
                    sessions.Remove(request.SessionId);
                    await session.DisposeAsync().ConfigureAwait(false);
                    return true;
                default: throw new ArgumentException("Unknown terminal operation.", nameof(operation));
            }
        }

        finally
        {
            changes.Release();
        }
    }

    /// <summary>古い終了済みセッションを回収し、稼働中のシェルは明示終了まで維持します。</summary>
    /// <returns>Host停止までの回収処理です。</returns>
    // 抑制理由: 上限寿命とHost停止時に辞書で保持した非同期資源をすべて回収します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "辞書から取り外す資源を保持します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "保持していたセッションを期限またはHost停止で解放します。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await changes.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    foreach (Guid id in sessions.Where(item => item.Value.View.Exited && TimeSpan.FromHours(1) < DateTimeOffset.UtcNow - item.Value.View.StartedAt).Select(item => item.Key).ToArray())
                    {
                        TerminalSession session = sessions[id];
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
            foreach (TerminalSession session in sessions.Values)
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

    /// <summary>Hostによる停止完了後に同期資源を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }
}
