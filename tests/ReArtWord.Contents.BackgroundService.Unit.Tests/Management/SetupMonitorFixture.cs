using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>実SQLiteの保守保留と、専用の操作ファイルだけを持つ監視環境です。</summary>
internal sealed class SetupMonitorFixture : IAsyncDisposable
{
    private readonly ManagementTestPeer peer;
    private readonly string root = Path.Combine(Path.GetTempPath(), "clutch-setup-monitor-" + Guid.NewGuid().ToString("N"));

    /// <summary>一つの保守操作と、OSを終了しない通知先を構成します。</summary>
    internal SetupMonitorFixture(bool failLog = false)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string clusterId = Guid.NewGuid().ToString("N");
        peer = new(root, clusterId, "peer", port, true);
        Job = new(Guid.NewGuid(), ESetupAction.Reinstall,
            new(root, root, clusterId, "host", "peer", 1, true, true, new("loopback", "127.0.0.1", "127.255.255.255", port), new(), true));
        DirectoryPath = SetupWorkspace.DirectoryFor(Job.OperationId);
        Directory.CreateDirectory(DirectoryPath);
        Log = new() { Throw = failLog };
        Lifetime = new(() => Assert.False(Maintenance.IsClosed));
        Monitor = new(peer.Runtime, Lifetime, Log);
    }

    internal SetupJob Job
    {
        get;
    }

    internal string DirectoryPath
    {
        get;
    }

    internal SetupManagement Monitor
    {
        get;
    }

    internal RecordingWorkflowLog Log
    {
        get;
    }

    internal MonitorLifetime Lifetime
    {
        get;
    }

    internal ExecutionMaintenanceGate Maintenance => peer.Runtime.Maintenance;

    /// <summary>実際の所有者IDで通常実行を保留します。</summary>
    /// <returns>保留の確認完了です。</returns>
    internal async Task InitializeAsync()
    {
        await Maintenance.LoadAsync().ConfigureAwait(false);
        Assert.True(await Maintenance.EnterAsync(Job.OperationId, "setup").ConfigureAwait(false));
    }

    /// <summary>この試験の操作と保存先だけを回収します。</summary>
    /// <returns>所有資源の解放完了です。</returns>
    public async ValueTask DisposeAsync()
    {
        Monitor.Dispose();
        await ((IAsyncDisposable)peer).DisposeAsync().ConfigureAwait(false);
        Directory.Delete(DirectoryPath, true);
        Directory.Delete(root, true);
    }

    /// <summary>Coreを終了せず、停止要求時点の保留解除を検査します。</summary>
    internal sealed class MonitorLifetime(Action stopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        internal int Stops
        {
            get; private set;
        }

        /// <summary>OSを操作せず停止要求を記録します。</summary>
        public void StopApplication()
        {
            stopping();
            Stops++;
        }
    }
}
