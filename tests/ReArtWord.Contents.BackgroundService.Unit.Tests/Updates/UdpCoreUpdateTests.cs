using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>HTTPなしの二つの実Coreを独立Updaterが最後まで更新します。</summary>
public sealed class UdpCoreUpdateTests
{
    /// <summary>入れ子の別SDK出力を除き、実行に必要なruntimeとlocale資産は残します。</summary>
    /// <returns>既存buildディレクトリに依存しない試験用の構成梱包境界です。</returns>
    [Fact]
    public async Task PackageExcludesNestedApplicationOutputAndKeepsRuntimeAssets()
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-update-package-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "package");
        string name = "Clutch-CS.CoreService";
        string native = "runtimes/" + RuntimeInformation.RuntimeIdentifier + "/native/fixture.dat";
        string[] paths = [Executable(name), name + ".deps.json", name + ".runtimeconfig.json", "win-x64/" + name + ".deps.json", "win-x64/" + name + ".runtimeconfig.json", "win-x64/duplicate.dll", native, "ja/fixture.resources.dll", "assets/" + name + ".deps.json", "assets/keep.dat"];
        try
        {
            foreach (string relative in paths)
            {
                string path = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "fixture asset").ConfigureAwait(true);
            }
            ReleaseManifest manifest = await PackageAsync(source, destination, "fixture").ConfigureAwait(true);
            Assert.Contains(manifest.Files, file => string.Equals(file.Path, Executable(name), StringComparison.Ordinal));
            Assert.Contains(manifest.Files, file => string.Equals(file.Path, native, StringComparison.Ordinal));
            Assert.Contains(manifest.Files, file => string.Equals(file.Path, "ja/fixture.resources.dll", StringComparison.Ordinal));
            Assert.Contains(manifest.Files, file => string.Equals(file.Path, "assets/keep.dat", StringComparison.Ordinal));
            Assert.Contains(manifest.Files, file => string.Equals(file.Path, "assets/" + name + ".deps.json", StringComparison.Ordinal));
            Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("win-x64/", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(destination, "win-x64")));
            Assert.True(File.Exists(Path.Combine(destination, native)));
            Assert.True(File.Exists(Path.Combine(destination, "ja/fixture.resources.dll")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>探索一回の期限と試験全体のキャンセルを区別し、稼働確認だけを再観測します。</summary>
    /// <returns>遅れて届く実UDP応答と呼び出し元キャンセルの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthObservationPreservesTheCallerDeadlineAsync(bool cancel)
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        ClientNetworkBinding binding = new(nic, "127.0.0.1", "127.255.255.255", port);
        string cluster = Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        UpdateObserver observer = new(binding, cluster);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable observerLifetime = observer.ConfigureAwait(true);
        UdpTransport target = new(new("target", binding.BindAddress, binding.BroadcastAddress, port, cluster), instance, TimeProvider.System);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = ((IAsyncDisposable)target).ConfigureAwait(true);
        target.Initialize(nic);
        ProcessIdentity identity = new(1234, DateTimeOffset.UtcNow);
        int inspections = 0;
        int mutations = 0;
        target.Received = async (message, _, token) =>
        {
            string inspect = "inspect";
            if (!string.Equals(message.Kind, inspect, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref mutations);
            }
            Interlocked.Increment(ref inspections);
            ManagementReply reply = new(EOutcome.Succeeded, UpdateObserver.Payload(new { ready = true, instanceId = instance, processId = identity.ProcessId, processStartedAt = identity.StartedAt }));
            ClusterResponse response = new(EOutcome.Succeeded, NodeValue.FromObject(reply));
            await target.SendAsync(WireMessage.Reply(message, "target", instance, JsonSerializer.SerializeToElement(response)), token).ConfigureAwait(false);
        };
        target.Start();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        List<ProcessIdentity> owned = [];
        Task<JsonElement> observation = HealthAsync(observer, "target", owned, deadline.Token);
        if (cancel)
        {
            await deadline.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation).ConfigureAwait(true);
            Assert.Empty(owned);
            Assert.Equal(0, inspections);
            Assert.Equal(0, mutations);
            return;
        }

        // 探索一回の上限（10秒）を超えるまで存在通知を止め、更新中の一時的な不在を再現します。
        await Task.Delay(TimeSpan.FromSeconds(11), deadline.Token).ConfigureAwait(true);
        long sequence = 0;
        while (!observation.IsCompleted)
        {
            PeerInfo peer = new("target", instance, "host", binding.BindAddress, port, EPeerStatus.Ready, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await target.SendAsync(new(Guid.NewGuid(), cluster, "target", instance, "hello", null, null, JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, "digest", "target", null, ++sequence))), deadline.Token).ConfigureAwait(true);
            await Task.Delay(20, deadline.Token).ConfigureAwait(true);
        }
        JsonElement health = await observation.ConfigureAwait(true);
        Assert.Equal(instance, health.GetProperty("instanceId").GetGuid());
        Assert.Equal(identity, Assert.Single(owned));
        Assert.True(0 < inspections);
        Assert.Equal(0, mutations);
    }

    /// <summary>固定対象とRID選択から自身入替・新実体確認までUDPだけで完了します。</summary>
    /// <returns>実プロセスと実UDPの確認完了です。</returns>
    // 抑制理由: 試験用の構成で起動したPIDとBCL実起動時刻だけを終了対象にします。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "自分の子process実体だけを追跡します。")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeClusterCompletesThroughObserver(bool bundle)
    {
        string root = Path.Combine(Path.GetTempPath(), "contents-udp-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        string core = Path.Combine(repository, "src", "ReArtWord.Contents.BackgroundService.CoreService", "bin", configuration, "net10.0");
        string updater = Path.Combine(repository, "src", "ReArtWord.Contents.BackgroundService.Updater", "bin", configuration, "net10.0", Executable("ReArtWord.Contents.BackgroundService.Updater"));
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        string clusterId = Guid.NewGuid().ToString("N");
        UpdateObserver observer = new(new(nic, "127.0.0.1", "127.255.255.255", port), clusterId);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(true);
        List<ProcessIdentity> owned = [];
        Dictionary<string, Guid> previous = new(StringComparer.Ordinal);
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(5));
        try
        {
            foreach (string peer in new[] { "update-a", "update-b" })
            {
                string local = Path.Combine(root, peer);
                string installation = Path.Combine(local, "installed");
                Directory.CreateDirectory(local);
                ReleaseManifest old = await PackageAsync(core, installation, "old", deadline.Token).ConfigureAwait(true);
                await LocalUpdateExecutor.WriteManifestAsync(old, installation, deadline.Token).ConfigureAwait(true);
                string fresh = "false";
                string first = "update-a";
                if (string.Equals(peer, first, StringComparison.Ordinal))
                {
                    fresh = "true";
                }
                string config = $"""
                    [cluster]
                    id = "{clusterId}"
                    peerId = "{peer}"
                    hostId = "{peer}"
                    interfaceId = "{nic}"
                    address = "127.0.0.1"
                    broadcast = "127.255.255.255"
                    port = {port}
                    initialize = {fresh}
                    [storage]
                    directory = "{Path.Combine(local, "state").Replace('\u005c', '/')}"
                    [logs]
                    interfaceId = "{nic}"
                    listenPort = 0
                    [updates]
                    installationRoot = "{installation.Replace('\u005c', '/')}"
                    stagingRoot = "{Path.Combine(local, "staging").Replace('\u005c', '/')}"
                    backupRoot = "{Path.Combine(local, "backups").Replace('\u005c', '/')}"
                    updaterExecutablePath = "{updater.Replace('\u005c', '/')}"
                    interfaceId = "{nic}"
                    stopTimeoutMilliseconds = 10000
                    startTimeoutMilliseconds = 20000
                    """;
                string path = Path.Combine(local, "contents.toml");
                await File.WriteAllTextAsync(path, config, deadline.Token).ConfigureAwait(true);
                ProcessStartInfo start = new(Path.Combine(installation, old.EntryPoint)) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--config");
                start.ArgumentList.Add(path);
                using Process child = Process.Start(start)!;
                owned.Add(new(child.Id, new DateTimeOffset(child.StartTime.ToUniversalTime())));
                JsonElement health = await HealthAsync(observer, peer, owned, deadline.Token).ConfigureAwait(true);
                previous.Add(peer, health.GetProperty("instanceId").GetGuid());
            }
            DateTimeOffset stopExpiry = DateTimeOffset.UtcNow.AddSeconds(10);
            Guid stopId = Guid.NewGuid();
            LocalUpdateStopRequest unverifiedStop = new(Guid.NewGuid(), "unverified", stopId, stopExpiry);
            ManagementCommand stop = new(stopId, clusterId, "update-a", previous["update-a"], stopExpiry, "updates.local.stop", UpdateObserver.Payload(unverifiedStop));
            ManagementReply denied = await observer.SendAsync(stop, false, deadline.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, denied.Outcome);
            Assert.False(denied.Value!.Json.GetBoolean());
            JsonElement stillRunning = await HealthAsync(observer, "update-a", owned, deadline.Token).ConfigureAwait(true);
            Assert.Equal(previous["update-a"], stillRunning.GetProperty("instanceId").GetGuid());
            string payload = Path.Combine(root, "update-a", "staging", "bundle", "current");
            ReleaseManifest manifest = await PackageAsync(core, payload, "new", deadline.Token).ConfigureAwait(true);
            string payloadRoot = payload;
            IReadOnlyList<ReleaseVariant>? variants = null;
            if (bundle)
            {
                payloadRoot = Path.GetDirectoryName(payload)!;
                variants = [new(manifest, "current")];
            }
            ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, payloadRoot, ["update-a", "update-b"], EUpdateFailurePolicy.RollbackSuccessful, PeerTimeoutMilliseconds: 60000, Variants: variants);
            ManagementReply accepted = await observer.SendAsync("update-a", previous["update-a"], "updates.start", UpdateObserver.Payload(request), false, deadline.Token).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
            ClusterUpdateStatus? result = null;
            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    foreach (string peer in previous.Keys)
                    {
                        await HealthAsync(observer, peer, owned, deadline.Token).ConfigureAwait(true);
                    }
                    Contracts.Cluster.PeerInfo current = await observer.FindAsync("update-a", cancellationToken: deadline.Token).ConfigureAwait(true);
                    ManagementReply status = await observer.SendAsync("update-a", current.InstanceId, "updates.read", UpdateObserver.Payload(request.OperationId), true, deadline.Token).ConfigureAwait(true);
                    result = status.Value?.Json.Deserialize<ClusterUpdateStatus>(ContractJson.Options);
                    if (result?.Completed == true)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // 稼働確認後に担当Coreが再起動しても、更新要求は再送せず同じ操作の結果だけを再照会します。
                }
                await Task.Delay(200, deadline.Token).ConfigureAwait(true);
            }
            Assert.True(result?.Outcome == EOutcome.Succeeded, result?.Reason + " / " + JsonSerializer.Serialize(result?.Peers));
            foreach (string peer in previous.Keys)
            {
                JsonElement health = await HealthAsync(observer, peer, owned, deadline.Token).ConfigureAwait(true);
                Assert.NotEqual(previous[peer], health.GetProperty("instanceId").GetGuid());
                Assert.Equal(JsonValueKind.Null, health.GetProperty("maintenance").ValueKind);
                Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root, peer, "installed", "release-marker.txt"), deadline.Token).ConfigureAwait(true));
            }
        }
        finally
        {
            foreach (ProcessIdentity identity in owned)
            {
                try
                {
                    using Process child = Process.GetProcessById(identity.ProcessId);
                    if (!child.HasExited && new DateTimeOffset(child.StartTime.ToUniversalTime()) == identity.StartedAt)
                    {
                        child.Kill(true);
                        await child.WaitForExitAsync().ConfigureAwait(true);
                    }
                }
                catch (ArgumentException)
                {
                    // 旧実体は既にUpdaterで終了しています。
                }
            }
            for (int attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100).ConfigureAwait(true);
                }
            }
        }
    }

    /// <summary>新実体を含む実稼働確認を観測し、cleanup対象を固定します。</summary>
    /// <returns>readyな実体です。</returns>
    private static async Task<JsonElement> HealthAsync(UpdateObserver observer, string peerId, List<ProcessIdentity> owned, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PeerInfo peer;
            try
            {
                peer = await observer.FindAsync(peerId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 更新中は探索一回の期限を超えてPeerが不在になり得るため、試験全体の期限内だけ再観測します。
                continue;
            }
            ManagementReply reply = await observer.SendAsync(peerId, peer.InstanceId, "health", null, true, cancellationToken).ConfigureAwait(false);
            if (reply.Value is not null)
            {
                JsonElement health = reply.Value.Json;
                owned.Add(new(health.GetProperty("processId").GetInt32(), health.GetProperty("processStartedAt").GetDateTimeOffset()));
                string ready = "ready";
                if (health.GetProperty(ready).GetBoolean())
                {
                    return health.Clone();
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>現RIDの実Coreだけを独立ペイロードへコピーします。</summary>
    /// <returns>コピー後実体のチェックサム一覧です。</returns>
    private static async Task<ReleaseManifest> PackageAsync(string source, string destination, string release, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        List<ReleaseFile> files = [];
        foreach (string file in PayloadFiles(source))
        {
            string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            string runtimes = "runtimes/";
            string currentRuntime = runtimes + RuntimeInformation.RuntimeIdentifier + "/";
            if (relative.StartsWith(runtimes, StringComparison.Ordinal) && !relative.StartsWith(currentRuntime, StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            byte[] bytes = await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
            files.Add(new(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        await File.WriteAllTextAsync(Path.Combine(destination, "release-marker.txt"), release, cancellationToken).ConfigureAwait(false);
        byte[] marker = System.Text.Encoding.UTF8.GetBytes(release);
        files.Add(new("release-marker.txt", marker.Length, Convert.ToHexString(SHA256.HashData(marker))));
        return new(release, RuntimeInformation.RuntimeIdentifier, Executable("Clutch-CS.CoreService"), files, ["old", "new"]);
    }

    /// <summary>トップ直下の独立アプリ出力を再梱包せず、通常の資産階層だけ列挙します。</summary>
    /// <returns>今のCore出力に属するファイルです。</returns>
    private static IEnumerable<string> PayloadFiles(string source)
    {
        foreach (string file in Directory.EnumerateFiles(source))
        {
            yield return file;
        }
        string application = "Clutch-CS.CoreService";
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string dependencies = Path.Combine(directory, application + ".deps.json");
            string runtimeConfiguration = Path.Combine(directory, application + ".runtimeconfig.json");
            if (File.Exists(dependencies) && File.Exists(runtimeConfiguration))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    /// <summary>対象OSのapphost名を選びます。</summary>
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
