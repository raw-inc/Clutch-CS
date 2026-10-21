using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>本番とは別の一時installation・UDPポートだけで更新される実Coreです。</summary>
internal sealed class RollingUpdatePeer : IAsyncDisposable
{
    private readonly string executable;
    private readonly string configuration;
    private readonly UdpObserver observer;

    /// <summary>独立した更新可能installationを用意します。</summary>
    internal RollingUpdatePeer(string root, string cluster, string peer, int port, string package, ReleaseManifest manifest, bool initialize)
    {
        Cluster = cluster;
        Peer = peer;
        string directory = Path.Combine(root, cluster, peer);
        string installed = Path.Combine(directory, "installed");
        Directory.CreateDirectory(installed);
        foreach (ReleaseFile file in manifest.Files)
        {
            string destination = Path.Combine(installed, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(package, file.Path), destination);
        }
        File.WriteAllText(Path.Combine(installed, "release-manifest.json"), JsonSerializer.Serialize(manifest, Contracts.Serialization.ContractJson.Options));
        executable = Path.Combine(installed, manifest.EntryPoint);
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        Binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        observer = new(new ObserverOptions(Guid.NewGuid().ToString("N"), Binding));
        string build = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string updater = Path.Combine(repository, "src", "ReArtWord.Contents.BackgroundService.Updater", "bin", build, "net10.0", Executable("ReArtWord.Contents.BackgroundService.Updater"));
        configuration = Path.Combine(directory, "contents.toml");
        File.WriteAllText(configuration, $$"""
            [cluster]
            id = "{{cluster}}"
            peerId = "{{peer}}"
            hostId = "{{peer}}"
            interfaceId = '{{nic}}'
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            port = {{port}}
            initialize = {{JsonSerializer.Serialize(initialize)}}
            [storage]
            directory = '{{Path.Combine(directory, "state").Replace(Path.DirectorySeparatorChar, '/')}}'
            [logs]
            interfaceId = '{{nic}}'
            listenPort = 0
            [updates]
            installationRoot = '{{installed.Replace(Path.DirectorySeparatorChar, '/')}}'
            stagingRoot = '{{Path.Combine(directory, "staging").Replace(Path.DirectorySeparatorChar, '/')}}'
            backupRoot = '{{Path.Combine(directory, "backups").Replace(Path.DirectorySeparatorChar, '/')}}'
            updaterExecutablePath = '{{updater.Replace(Path.DirectorySeparatorChar, '/')}}'
            interfaceId = '{{nic}}'
            stopTimeoutMilliseconds = 10000
            startTimeoutMilliseconds = 20000
            """);
    }

    /// <summary>試験クラスタです。</summary>
    internal string Cluster
    {
        get;
    }

    /// <summary>試験host兼ピアです。</summary>
    internal string Peer
    {
        get;
    }

    /// <summary>本番と重ならない試験NIC・ポートです。</summary>
    internal ClientNetworkBinding Binding
    {
        get;
    }

    /// <summary>実Coreを起動します。</summary>
    /// <returns>healthで確認した初期実体です。</returns>
    internal async Task<Guid> StartAsync(CancellationToken cancellationToken = default)
    {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configuration);
        using Process process = Process.Start(start) ?? throw new IOException("Fixture did not start.");
        Guid instance = Guid.Empty;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement health = await HealthAsync(token).ConfigureAwait(false);
            instance = health.GetProperty("instanceId").GetGuid();
            return health.GetProperty("ready").GetBoolean();
        }, cancellationToken).ConfigureAwait(false);
        return instance;
    }

    /// <summary>発見した実体のhealthを実UDPで読みます。</summary>
    /// <returns>当該試験ピアの状態です。</returns>
    internal async Task<JsonElement> HealthAsync(CancellationToken cancellationToken = default)
    {
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? health = null;
        await CorePeer.WaitAsync(async token =>
        {
            Contracts.Cluster.PeerInfo? peer = observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, Cluster, StringComparison.Ordinal))
                .SelectMany(cluster => cluster.Peers).Where(item => string.Equals(item.PeerId, Peer, StringComparison.Ordinal)).OrderByDescending(item => item.LastSeen).FirstOrDefault();
            if (peer is null)
            {
                return false;
            }
            ManagementReply reply = await observer.InspectAsync(new(Guid.NewGuid(), Cluster, Peer, peer.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(5), "health", null), token).ConfigureAwait(false);
            if (reply.Outcome != Contracts.Execution.EOutcome.Succeeded || reply.Value is null)
            {
                return false;
            }
            health = reply.Value.Json;
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return health!.Value;
    }

    /// <summary>Coreへ接続できない場合も、更新プロセスが保存した結果を確認します。</summary>
    /// <returns>更新記録の最終結果、記録がない場合はnullです。</returns>
    internal async Task<UpdatePeerResult?> ReadUpdateResultAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Path.GetDirectoryName(configuration)!, "staging", "operations", operationId.ToString("N"), "journal.json");
        if (!File.Exists(path))
        {
            return null;
        }
        DurableEntry? entry = await new JsonFileRepository(path).ReadAsync("peer:" + Peer + ":updates", "operation", cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<LocalUpdateRecord>(ContractJson.Options)?.Result;
    }

    /// <summary>この試験のinstallationから動くプロセスだけを終了します。</summary>
    /// <returns>新旧試験実体と監視ソケットの終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (Process process in Process.GetProcessesByName("Clutch-CS.CoreService"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 別ユーザーのプロセスは参照不能でも終了対象にしません。
                }
            }
        }
        await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>既存ビルドの現在RIDの実行資産を、一つの検証可能な配布元へ梱包します。</summary>
    /// <returns>Updaterで検証するハッシュ一覧です。</returns>
    internal static async Task<ReleaseManifest> PackageAsync(string destination, CancellationToken cancellationToken = default)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string source = Path.GetFullPath("../../../../../src/ReArtWord.Contents.BackgroundService.CoreService/bin/" + configuration + "/net10.0", AppContext.BaseDirectory);
        IEnumerable<string> files = Directory.EnumerateFiles(source).Concat(Directory.EnumerateDirectories(source)
            .Where(directory => !File.Exists(Path.Combine(directory, "Clutch-CS.CoreService.runtimeconfig.json")))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)));
        List<ReleaseFile> entries = [];
        string runtimes = "runtimes/", selectedRuntime = "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/";
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(runtimes, StringComparison.Ordinal) && !relative.StartsWith(selectedRuntime, StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            using FileStream stream = File.OpenRead(target);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            entries.Add(new(relative, stream.Length, hash));
        }
        return new("old", RuntimeInformation.RuntimeIdentifier, Executable("Clutch-CS.CoreService"), entries, ["old", "new"]);
    }

    /// <summary>各OSのapphost名です。</summary>
    /// <returns>実行ファイル名です。</returns>
    private static string Executable(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return name + ".exe";
        }
        return name;
    }
}
