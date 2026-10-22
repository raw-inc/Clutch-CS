using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>OS管理を変更せず配置処理の境界を観測します。</summary>
internal sealed class RecordingPlatform : IInstallationPlatform
{
    /// <summary>アクセス権の実処理は専用試験で確認し、ここでは配置先だけを作ります。</summary>
    public void PrepareDirectory(string path, bool perUser, bool privateData = false, bool recursive = false)
    {
        if (UseFilePermissions)
        {
            new InstallationPlatform().PrepareDirectory(path, perUser, privateData, recursive);
            return;
        }
        Directory.CreateDirectory(path);
    }

    public bool UseFilePermissions
    {
        get; set;
    }

    public WebClientInstallation? WebClient
    {
        get; private set;
    }

    public string ClientRoot
    {
        get; set;
    } = string.Empty;

    /// <summary>実PCの共有配置先を試験専用領域へ置き換えます。</summary>
    /// <returns>試験内のclient配置先です。</returns>
    public string WebClientRoot(bool perUser) => ClientRoot;

    /// <summary>実processへ停止要求を出しません。</summary>
    /// <returns>停止境界の完了です。</returns>
    public Task StopWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClientStops++;
        return Task.CompletedTask;
    }

    /// <summary>Webクライアントの停止要求数です。</summary>
    public int ClientStops
    {
        get; private set;
    }

    /// <summary>試験が観測するWebクライアントの実体です。</summary>
    public ProcessIdentity ClientProcess
    {
        get; set;
    } = new(17, DateTimeOffset.UnixEpoch);

    /// <summary>配置されたマニフェストと試験用の実体を返します。</summary>
    /// <returns>試験内の起動確認です。</returns>
    public async Task<WebClientUpdateInfo> InspectWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        Contracts.Updates.Releases.ReleaseManifest manifest = await Infrastructure.Updates.Local.LocalUpdateExecutor.ReadManifestAsync(WebClientCatalog.ProgramRoot(client), cancellationToken).ConfigureAwait(false);
        return new(client, new(ClientProcess, manifest.ExpectedBuildVersion ?? "fixture"));
    }

    /// <summary>OSの起動を行わず、版ごとの失敗と新実体を模擬します。</summary>
    /// <returns>試験内の開始です。</returns>
    public async Task StartWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        await RegisterWebClientAsync(client, cancellationToken).ConfigureAwait(false);
        ClientCalls.Add("start");
        if (StageBehavior is not null)
        {
            await StageBehavior("client-start").ConfigureAwait(false);
        }
        ClientProcess = new(ClientProcess.ProcessId + 1, ClientProcess.StartedAt.AddSeconds(1));
    }

    /// <summary>この試験内で登録されたclientだけを返します。</summary>
    /// <returns>試験内の登録です。</returns>
    public Task<WebClientInstallation?> FindWebClientAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WebClient);
    }

    /// <summary>OS索引を変更せず登録を記録します。</summary>
    /// <returns>試験内の登録完了です。</returns>
    public Task RegisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WebClient = client;
        ClientCalls.Add("register");
        return Task.CompletedTask;
    }

    /// <summary>指定された試験内の登録だけを解除します。</summary>
    /// <returns>試験内の解除完了です。</returns>
    public Task UnregisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClientCalls.Add("unregister");
        if (WebClient?.InstallationId == client.InstallationId)
        {
            WebClient = null;
        }
        return Task.CompletedTask;
    }

    public List<string> ClientCalls
    {
        get;
    } = [];

    public List<string> Calls
    {
        get;
    } = [];

    public bool FailReady
    {
        get; set;
    }

    public Func<string, Task>? StageBehavior
    {
        get; set;
    }

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task RegisterAsync(InstallationRecord record, CancellationToken cancellationToken = default) => RecordAsync("register", cancellationToken);

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task StartAsync(InstallationRecord record, CancellationToken cancellationToken = default) => RecordAsync("start", cancellationToken);

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task StopAsync(InstallationRecord record, ProcessIdentity? process, CancellationToken cancellationToken = default) => RecordAsync("stop", cancellationToken);

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task UnregisterAsync(InstallationRecord record, CancellationToken cancellationToken = default) => RecordAsync("unregister", cancellationToken);

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    public Task VerifyAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        if (FailReady)
        {
            throw new IOException("Ready was not confirmed.");
        }
        return RecordAsync("ready", cancellationToken);
    }

    /// <summary>配置試験の実行境界を記録または準備します。</summary>

    /// <returns>試験用処理の完了です。</returns>
    private Task RecordAsync(string stage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(stage);
        return StageBehavior?.Invoke(stage) ?? Task.CompletedTask;
    }
}
