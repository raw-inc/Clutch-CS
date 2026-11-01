using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>電源コマンドを持たない専用プロセスを、通常の管理輸送だけで操作します。</summary>
internal sealed class PowerSchedulePeer : IAsyncDisposable
{
    private readonly UdpObserver observer;
    private readonly string cluster;
    private readonly string id;
    private readonly string configuration;
    private Process? process;

    /// <summary>保存先とポートを、試験が所有する値に限定します。</summary>
    internal PowerSchedulePeer(string root, string cluster, string id, int port, int destinationPort, UdpObserver observer, bool initialize)
    {
        this.observer = observer;
        this.cluster = cluster;
        this.id = id;
        DirectoryPath = Path.Combine(root, id);
        Directory.CreateDirectory(DirectoryPath);
        configuration = Path.Combine(DirectoryPath, "fixture.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new { directory = DirectoryPath, cluster, peer = id, host = "host-" + id, port, destinationPort, initialize }));
    }

    /// <summary>この試験だけのDB・無害な操作記録の保存先です。</summary>
    internal string DirectoryPath
    {
        get;
    }

    /// <summary>最新の実体識別子を管理輸送の発見情報から取得します。</summary>
    internal PeerInfo? Current => observer.GetClusters().Where(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal))
        .SelectMany(item => item.Peers).FirstOrDefault(item => string.Equals(item.PeerId, id, StringComparison.Ordinal));

    /// <summary>本番Coreを起動せず、同じロジックを持つ電源代替fixtureを開始します。</summary>
    internal void Start()
    {
        string build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string name = "ContentsDesktopFixture";
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        string executable = Path.GetFullPath("../../../../ReArtWord.Contents.BackgroundService.Unit.Tests/Management/FixtureHost/bin/" + build + "/net10.0/" + name, AppContext.BaseDirectory);
        Assert.True(File.Exists(executable), "Build the isolated power schedule fixture before running this test.");
        File.Delete(Path.Combine(DirectoryPath, "stop"));

        // アプリホスト経由で起動し、検証用のDOTNET_ROOTを子プロセスにも適用する。
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("power-schedule");
        start.ArgumentList.Add(configuration);
        process = Process.Start(start) ?? throw new IOException("Power schedule fixture did not start.");
    }

    /// <summary>新しい実体を確認する前に古い発見記録を採用しません。</summary>
    /// <returns>実プロセスがReadyとなるまでの待機です。</returns>
    internal async Task ReadyAsync(Guid? previous = null, CancellationToken cancellationToken = default)
    {
        while (Current is not { Status: EPeerStatus.Ready } found || found.InstanceId == previous)
        {
            Assert.NotNull(process);
            Assert.False(process.HasExited, "Power schedule fixture exited before readiness.");
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>試験操作も通常の要求と結果変換を通します。</summary>
    /// <returns>ドメインの結果です。</returns>
    internal async Task<T> SendAsync<T>(string operation, object? payload = null, bool write = false, bool transient = false, CancellationToken cancellationToken = default)
    {
        PeerInfo peer = Current ?? throw new InvalidOperationException("Test peer has not been discovered.");
        ManagementCommand command = new(Guid.NewGuid(), cluster, id, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(1), operation, ContractJson.ToValue(payload));
        ManagementReply reply;
        if (transient)
        {
            reply = await observer.NotifyAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (write)
            {
                reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reply = await observer.InspectAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        Assert.True(reply.Outcome == EOutcome.Succeeded, operation + ": " + reply.Reason);
        return reply.Value!.Json.Deserialize<T>(ContractJson.Options) ?? throw new InvalidDataException("Fixture response is empty.");
    }

    /// <summary>試験が作った子プロセスだけを終了します。</summary>
    /// <returns>OS電源を変更せず、所有processの終了です。</returns>
    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            process.Dispose();
            process = null;
        }
    }

    /// <summary>試験後に実通信を残しません。</summary>
    /// <returns>所有processの回収です。</returns>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
