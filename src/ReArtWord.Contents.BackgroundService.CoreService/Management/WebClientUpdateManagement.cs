using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.CoreService.Windows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>このCoreに関連付けられたWebクライアントの更新を受け付けます。</summary>
internal sealed class WebClientUpdateManagement : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ServiceRuntime runtime;
    private readonly SetupManagement setup;
    private readonly UpdateStreamReceiver stream;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>既存の管理通信とTCP転送を、クライアント専用の配置へ接続します。</summary>
    public WebClientUpdateManagement(ServiceRuntime runtime, SetupManagement setup)
    {
        this.runtime = runtime;
        this.setup = setup;
        stream = new(async (operation, token) => await (await WorkspaceAsync(token).ConfigureAwait(false)).ReadTransferAsync(operation, token).ConfigureAwait(false),
            () => runtime.Configuration.Updates, _ => null, runtime.Cluster.LocalInstanceId);
    }

    /// <summary>確認、準備、転送、実行と結果照会を分けて登録します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.Read("setup.client.info", async (_, token) => await WebClientUpdateProbe.ReadAsync(await ClientAsync(token).ConfigureAwait(false), token).ConfigureAwait(false));
        registry.Write("setup.client.prepare", async (body, token) => await PrepareAsync(ManagementRegistry.Body<WebClientUpdateRequest>(body), token).ConfigureAwait(false));
        registry.ReadValue("setup.client.stream.capabilities", _ => stream.Capabilities);
        registry.Write("setup.client.stream.open", async (body, token) => await stream.OpenAsync(ManagementRegistry.OperationId(body),
            runtime.Cluster.ManagementSourceAddress ?? throw new InvalidOperationException("The sender could not be confirmed."), token).ConfigureAwait(false));
        registry.ReadValue("setup.client.stream.read", body => stream.Read(ManagementRegistry.OperationId(body)));
        registry.Write("setup.client.start", async (body, token) => await StartAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
        registry.Read("setup.client.result", async (body, token) => await ResultAsync(ManagementRegistry.OperationId(body), token).ConfigureAwait(false));
    }

    /// <summary>Coreの導入記録が所有するクライアントだけを選びます。</summary>
    /// <returns>現行のクライアント導入記録です。</returns>
    private async Task<WebClientInstallation> ClientAsync(CancellationToken cancellationToken = default)
    {
        InstallationRecord record = await setup.ReadAsync(cancellationToken).ConfigureAwait(false);
        WebClientInstallation linked = record.WebClient ?? throw new InvalidDataException("This Core has no associated web client.");
        WebClientInstallation current = await WebClientCatalog.ReadAsync(linked.Root, cancellationToken).ConfigureAwait(false);
        if (current.InstallationId != linked.InstallationId || current.PerUser != linked.PerUser || current.RunAsService != linked.RunAsService
            || !string.Equals(current.ProgramDirectory, linked.ProgramDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The associated web client was replaced. Select it again.");
        }
        return current;
    }

    /// <summary>Core自身の入替先を共有せず、関連クライアントの操作領域を取得します。</summary>
    /// <returns>固定の操作領域です。</returns>
    private async Task<WebClientUpdateWorkspace> WorkspaceAsync(CancellationToken cancellationToken = default)
        => new(await ClientAsync(cancellationToken).ConfigureAwait(false), runtime.Configuration.Updates, runtime.Cluster.LocalPeerId, runtime.Cluster.LocalInstanceId);

    /// <summary>稼働実体を確認し、転送内容を一度だけ固定します。</summary>
    /// <returns>転送の準備結果です。</returns>
    private async Task<WebClientUpdateJob> PrepareAsync(WebClientUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Remote web client updates require Windows. Run the Installer on the target PC.");
        }
        WebClientInstallation client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        WebClientUpdateInfo current = await WebClientUpdateProbe.ReadAsync(client, cancellationToken).ConfigureAwait(false);
        return await new WebClientUpdateWorkspace(client, runtime.Configuration.Updates, runtime.Cluster.LocalPeerId, runtime.Cluster.LocalInstanceId)
            .PrepareAsync(request, current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ハッシュ確認済みのhelperへ引き継ぎ、同じ操作を再実行しません。</summary>
    /// <returns>独立helperの受付結果です。</returns>
    private async Task<SetupResult> StartAsync(Guid operation, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebClientUpdateWorkspace workspace = await WorkspaceAsync(cancellationToken).ConfigureAwait(false);
            UpdateLayout layout = workspace.Layout(operation);
            WebClientUpdateJob job = await workspace.ReadAsync(operation, cancellationToken).ConfigureAwait(false);
            string claim = Path.Combine(layout.OperationRoot, "dispatched");
            layout.Validate(claim);
            if (File.Exists(claim))
            {
                return await ResultAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            string helper = await workspace.PrepareHelperAsync(operation, cancellationToken).ConfigureAwait(false);
            using (FileStream once = new(claim, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
            {
                once.WriteByte(1);
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Remote web client updates require Windows. Run the Installer on the target PC.");
            }
            try
            {
                using Process process = WindowsClientUpdateLaunch.Start(job, helper, layout.JobPath);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                SetupResult failed = new(operation, EOutcome.NotExecuted, "helper", exception.Message, job.Installation.Root);
                await InstallationFiles.WriteAsync(Path.Combine(layout.OperationRoot, "result.json"), failed, CancellationToken.None).ConfigureAwait(false);
                return failed;
            }
            return new(operation, EOutcome.Unknown, "accepted", "The web client updater has started.", job.Installation.Root, false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>保存済み結果を返し、結果のない停止を成功とみなしません。</summary>
    /// <returns>実結果または明示的な未確認状態です。</returns>
    private async Task<SetupResult> ResultAsync(Guid operation, CancellationToken cancellationToken = default)
    {
        WebClientUpdateWorkspace workspace = await WorkspaceAsync(cancellationToken).ConfigureAwait(false);
        WebClientUpdateJob job = await workspace.ReadAsync(operation, cancellationToken).ConfigureAwait(false);
        UpdateLayout layout = workspace.Layout(operation);
        string resultPath = Path.Combine(layout.OperationRoot, "result.json");
        layout.Validate(resultPath);
        if (File.Exists(resultPath))
        {
            SetupResult result = JsonSerializer.Deserialize<SetupResult>(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
            if (result.OperationId != operation)
            {
                throw new InvalidDataException("The web client update result belongs to another operation.");
            }
            return result;
        }
        return new(operation, EOutcome.Unknown, "pending", "No final web client update result is available.", job.Installation.Root, false);
    }

    /// <summary>Coreの寿命内でのみ転送を受け付けます。</summary>
    /// <returns>受信終了です。</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => stream.RunAsync(stoppingToken);

    /// <summary>受付の同期資源を解放します。</summary>
    public override void Dispose()
    {
        gate.Dispose();
        base.Dispose();
    }
}
