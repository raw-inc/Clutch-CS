using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>実UDP子プロセスと入替専用rootを試験ごとに分離します。</summary>
internal sealed class UpdateTestScope : IDisposable
{
    private readonly List<ProcessIdentity> owned = [];
    private readonly string fixtureDirectory;

    /// <summary>実OSサービスに触れない直接プロセスの配置を作ります。</summary>
    internal UpdateTestScope()
    {
        Root = Path.Combine(Path.GetTempPath(), "contents-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Close();
        string nic = NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        Options = new UpdatesOptions(Path.Combine(Root, "installed"), Path.Combine(Root, "staging"), Path.Combine(Root, "backups"), StopTimeoutMilliseconds: 1000, StartTimeoutMilliseconds: 8000, ForceStopOnTimeout: true, InterfaceId: nic, LocalAddress: "127.0.0.1", ClusterId: "fixture-cluster", BroadcastAddress: "127.255.255.255", ManagementPort: Port);
        Directory.CreateDirectory(Options.StagingRoot!);
        DotnetPath = Environment.GetEnvironmentVariable("CONTENTS_TEST_DOTNET") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!OperatingSystem.IsWindows())
        {
            DotnetPath = Environment.GetEnvironmentVariable("CONTENTS_TEST_DOTNET") ?? "/usr/share/dotnet/dotnet";
        }
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        fixtureDirectory = Path.Combine(repository, "tests", "ReArtWord.Contents.BackgroundService.Unit.Tests", "Updates", "FixtureHost", "bin", configuration, "net10.0");
        UpdaterAssembly = Path.Combine(repository, "src", "ReArtWord.Contents.BackgroundService.Updater", "bin", configuration, "net10.0", "ReArtWord.Contents.BackgroundService.Updater.dll");
    }

    /// <summary>この試験の操作rootです。</summary>
    internal string Root
    {
        get;
    }

    /// <summary>実子プロセスに使用するポートです。</summary>
    internal int Port
    {
        get;
    }

    /// <summary>実dotnet hostです。</summary>
    internal string DotnetPath
    {
        get;
    }

    /// <summary>build済みstandaloneUpdaterです。</summary>
    internal string UpdaterAssembly
    {
        get;
    }

    /// <summary>三つの独立rootと直接プロセス操作だけの設定です。</summary>
    internal UpdatesOptions Options
    {
        get;
    }

    /// <summary>behavior以外は同じ実アプリケーションから一リリースを作ります。</summary>
    /// <returns>チェックサム付きマニフェストとペイロードです。</returns>
    internal async Task<(ReleaseManifest Manifest, string Root)> PackageAsync(string version, bool ready, CancellationToken cancellationToken = default)
    {
        string payload = Path.Combine(Options.StagingRoot!, version);
        Directory.CreateDirectory(payload);
        foreach (string source in Directory.EnumerateFiles(fixtureDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(fixtureDirectory, source);
            string runtimePrefix = "runtimes" + Path.DirectorySeparatorChar;
            string currentRuntime = runtimePrefix + RuntimeInformation.RuntimeIdentifier + Path.DirectorySeparatorChar;
            if (relative.StartsWith(runtimePrefix, StringComparison.Ordinal) && !relative.StartsWith(currentRuntime, StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.Combine(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        await File.WriteAllTextAsync(Path.Combine(payload, "behavior.json"), JsonSerializer.Serialize(new { ready, version }), cancellationToken).ConfigureAwait(false);
        List<ReleaseFile> files = [];
        foreach (string path in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            files.Add(new ReleaseFile(Path.GetRelativePath(payload, path).Replace(Path.DirectorySeparatorChar, '/'), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        return (new ReleaseManifest(version, RuntimeInformation.RuntimeIdentifier, "ContentsUpdateFixture.dll", files, ["v1", "v2"], version), payload);
    }

    /// <summary>初期リリースを実際に起動して旧実体を取得します。</summary>
    /// <returns>停止前の稼働確認とプロセスの識別子です。</returns>
    // 抑制理由: 実子プロセスのPID再利用を防ぐためBCL時刻をDateTimeOffsetへ即時変換します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験対象の実起動識別子を保存します。")]
    internal async Task<(Guid Instance, ProcessIdentity Process)> StartOriginalAsync(ReleaseManifest manifest, string payload, CancellationToken cancellationToken = default)
    {
        await ReleaseVerifier.CopyAsync(manifest, payload, Options.InstallationRoot!, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(manifest, Options.InstallationRoot!, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new(DotnetPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Options.InstallationRoot! };
        start.ArgumentList.Add(Path.Combine(Options.InstallationRoot!, manifest.EntryPoint));
        start.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("fixture-peer");
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        ProcessIdentity identity = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
        owned.Add(identity);
        JsonElement health = await HealthAsync(cancellationToken).ConfigureAwait(false);
        return (health.GetProperty("instanceId").GetGuid(), identity);
    }

    /// <summary>この試験用の構成へだけ更新する固定ジョブを作ります。</summary>
    /// <returns>実子プロセスの入替入力です。</returns>
    internal LocalUpdateJob Job(ReleaseManifest manifest, string payload, Guid instance, ProcessIdentity process)
    {
        return new LocalUpdateJob(Guid.NewGuid(), "fixture-peer", instance, process, manifest, payload, Options, [Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixture-peer"], DotnetPath);
    }

    /// <summary>最後の実子プロセスを追跡して漏れなくcleanupします。</summary>
    /// <returns>実UDP 稼働確認です。</returns>
    internal async Task<JsonElement> HealthAsync(CancellationToken cancellationToken = default)
    {
        UpdateObserver observer = new(UpdateObserver.Binding(Options), Options.ClusterId!);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        while (!linked.IsCancellationRequested)
        {
            Contracts.Cluster.PeerInfo peer = await observer.FindAsync("fixture-peer", cancellationToken: linked.Token).ConfigureAwait(false);
            ManagementReply reply = await observer.SendAsync("fixture-peer", peer.InstanceId, "health", null, true, linked.Token).ConfigureAwait(false);
            if (reply.Outcome == EOutcome.Succeeded && reply.Value is not null)
            {
                JsonElement health = reply.Value.Json.Clone();
                owned.Add(new ProcessIdentity(health.GetProperty("processId").GetInt32(), health.GetProperty("processStartedAt").GetDateTimeOffset()));
                return health;
            }
            await Task.Delay(50, linked.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("fixture healthが起動しませんでした。");
    }

    /// <summary>自分の試験用の構成だけを正常停止して継承stdoutを閉じます。</summary>
    /// <returns>元操作記録の停止受理です。</returns>
    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        UpdateObserver observer = new(UpdateObserver.Binding(Options), Options.ClusterId!);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        Contracts.Cluster.PeerInfo peer = await observer.FindAsync("fixture-peer", cancellationToken: cancellationToken).ConfigureAwait(false);
        await observer.SendAsync("fixture-peer", peer.InstanceId, "updates.local.stop", null, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>この試験が実起動したプロセスと専用rootだけを削除します。</summary>
    // 抑制理由: PID再利用先を終了しないためBCL時刻を取得して即時変換・照合します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "試験が所有するprocessの実体だけを終了します。")]
    public void Dispose()
    {
        string root = ValidateCleanupRoot();
        foreach (ProcessIdentity identity in owned.Distinct())
        {
            Process process;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
            }
            catch (ArgumentException)
            {
                // PIDが存在しない場合だけ、元の実体の終了として扱います。
                continue;
            }
            using (process)
            {
                if (!process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) != identity.StartedAt)
                {
                    // 再利用されたPIDは別の実体なので終了しません。
                    continue;
                }
                if (!process.WaitForExit(1000))
                {
                    process.Kill(true);
                    if (!process.WaitForExit(5000))
                    {
                        throw new TimeoutException($"Fixture process exit is unconfirmed: PID={identity.ProcessId}, startedAt={identity.StartedAt:O}, retainedRoot={root}");
                    }
                }
            }
        }
        Stopwatch elapsed = Stopwatch.StartNew();
        bool deleted = false;
        while (!deleted)
        {
            try
            {
                Directory.Delete(root, true);
                deleted = true;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xFFFF) is 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                // 全所有実体の終了確認後だけ、OSの共有・lock違反を有限に待ちます。
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>再帰削除は生成した直下の試験rootだけに限定し、linkを辿りません。</summary>
    /// <returns>検証済みの絶対パスです。</returns>
    private string ValidateCleanupRoot()
    {
        string root = Path.GetFullPath(Root);
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string prefix = "contents-update-test-";
        string identityFormat = "N";
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], identityFormat, out _))
        {
            throw new IOException("Fixture cleanup root is outside the generated temporary directory: " + root);
        }
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Fixture cleanup refuses a linked directory: " + directory);
            }
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                pending.Push(child);
            }
        }
        return root;
    }
}
