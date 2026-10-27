using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>本番と別の待受・保存先・実行物を持つ更新可能なClientHostです。</summary>
internal sealed class ClientUpdateInstallation : IAsyncDisposable
{
    private readonly List<Process> helpers = [];
    private Process? original;

    /// <summary>試験専用の作業rootです。</summary>
    internal string Root 
    {
        get;
    } = Path.Combine(Path.GetTempPath(), "clutch-client-update-" + Guid.NewGuid().ToString("N"));

    /// <summary>変更操作は呼び出し元の試験期限まで待ち、短い照会期限を適用しません。</summary>
    internal HttpClient Http 
    {
        get;
    } = new() { BaseAddress = new Uri($"http://127.0.0.1:{CorePeer.AvailablePort(tcp: true)}/"), Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>入替から独立した状態保存先です。</summary>
    internal string State => Path.Combine(Root, "state");

    /// <summary>自動起動が参照し続ける固定パスです。</summary>
    internal string Installed => Path.Combine(Root, "installed");

    /// <summary>配布元のClientHostディレクトリです。</summary>
    internal string Payload => Path.Combine(Root, "client");

    /// <summary>試験用リリースの全内容です。</summary>
    internal ReleaseManifest Manifest 
    {
        get; private set;
    } = null!;

    /// <summary>実ビルドを複製し、Updaterを同梱して旧ClientHostを起動します。</summary>
    /// <returns>独立した実ClientHostの起動です。</returns>
    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await CopyBuildAsync("ClientHost", Payload, cancellationToken).ConfigureAwait(false);
        string helper = Path.Combine(Payload, "updater");
        await CopyBuildAsync("Updater", helper, cancellationToken).ConfigureAwait(false);
        ReleaseManifest updater = await ManifestAsync(helper, "Updater", cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(updater, helper, cancellationToken).ConfigureAwait(false);
        Manifest = await ManifestAsync(Payload, "ClientHost", cancellationToken).ConfigureAwait(false);
        await ReleaseVerifier.CopyAsync(Manifest, Payload, Installed, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(Manifest with { ReleaseId = "old" }, Installed, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new(Path.Combine(Installed, Manifest.EntryPoint)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Installed };
        foreach (string argument in new[] { "--listen", Http.BaseAddress!.AbsoluteUri, "--state", State })
        {
            start.ArgumentList.Add(argument);
        }
        original = Process.Start(start) ?? throw new IOException("Test ClientHost did not start.");
        await CorePeer.WaitAsync(async token =>
        {
            try
            {
                return (await HealthAsync(token).ConfigureAwait(false)).Process.ProcessId == original.Id;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>直接経路で応答中の実体と稼働版を確認します。</summary>
    /// <returns>現在のClientHostです。</returns>
    internal async Task<ClientUpdateHealth> HealthAsync(CancellationToken cancellationToken = default)
        => await ReadAsync<ClientUpdateHealth>("client/health", cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("ClientHost health is missing.");

    /// <summary>再接続中の照会は一回ごとに打ち切り、呼び出し元の期限内で再確認できるようにします。</summary>
    /// <returns>確認できた応答です。</returns>
    internal async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(TimeSpan.FromSeconds(3));
        return await Http.GetFromJsonAsync<T>(new Uri(path, UriKind.Relative), ContractJson.Options, request.Token).ConfigureAwait(false);
    }

    /// <summary>実行せず独立Updaterの固定jobと候補を用意します。</summary>
    /// <returns>一回のjobです。</returns>
    internal async Task<ClientUpdateJob> PrepareAsync(ReleaseManifest release, int timeoutMilliseconds = 8000, CancellationToken cancellationToken = default)
    {
        ClientUpdateJob job = new(Guid.NewGuid(), release, Installed, State, Http.BaseAddress!, (await HealthAsync(cancellationToken).ConfigureAwait(false)).Process, timeoutMilliseconds);
        ClientUpdateFiles files = new(job);
        Directory.CreateDirectory(files.Operation);
        await ReleaseVerifier.CopyAsync(release, Payload, files.Candidate, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(release, files.Candidate, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(files.JobPath, JsonSerializer.Serialize(job, ContractJson.Options), cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>旧プロセスから独立する実Updaterを起動します。</summary>
    /// <returns>終了を確認できる実プロセスです。</returns>
    internal Process StartUpdater(ClientUpdateJob job)
    {
        ClientUpdateFiles files = new(job);
        string executable = Path.Combine(Payload, "updater", Executable("Updater"));
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Operation };
        start.ArgumentList.Add("--client-job");
        start.ArgumentList.Add(files.JobPath);
        Process process = Process.Start(start) ?? throw new IOException("Test updater did not start.");
        helpers.Add(process);
        return process;
    }

    /// <summary>試験が起動した元ClientHostだけを終了します。</summary>
    /// <returns>元実体の終了確認です。</returns>
    internal async Task StopOriginalAsync(CancellationToken cancellationToken = default)
    {
        if (original is not null && !original.HasExited)
        {
            original.Kill(false);
            await original.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>所有helperと、この試験の固定パスから起動したClientHostだけを終了します。</summary>
    /// <returns>試験プロセスの終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (Process helper in helpers)
        {
            if (!helper.HasExited)
            {
                helper.Kill(false);
                await helper.WaitForExitAsync().ConfigureAwait(false);
            }
            helper.Dispose();
        }
        await StopOriginalAsync().ConfigureAwait(false);
        original?.Dispose();
        foreach (Process process in Process.GetProcessesByName("Clutch-CS.ClientHost"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Path.Combine(Installed, Manifest.EntryPoint), StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(false);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }
        Http.Dispose();
    }

    /// <summary>試験用に対象RIDのビルドだけを複製します。</summary>
    /// <returns>独立した配布ディレクトリです。</returns>
    private static async Task CopyBuildAsync(string component, string destination, CancellationToken cancellationToken = default)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string source = Path.GetFullPath("../../../../../src/ReArtWord.Contents.BackgroundService." + component + "/bin/" + configuration + "/net10.0", AppContext.BaseDirectory);
        string runtimes = "runtimes/", selected = runtimes + RuntimeInformation.RuntimeIdentifier + "/";
        IEnumerable<string> buildFiles = Directory.EnumerateFiles(source).Concat(Directory.EnumerateDirectories(source)
            .Where(directory => !File.Exists(Path.Combine(directory, AssemblyName(component) + ".runtimeconfig.json")))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)));
        foreach (string path in buildFiles)
        {
            string relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(runtimes, StringComparison.Ordinal) && !relative.StartsWith(selected, StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream input = File.OpenRead(path);
            using FileStream output = File.Create(target);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実内容とassembly版から配布契約を作ります。</summary>
    /// <returns>検証可能な試験マニフェストです。</returns>
    private static async Task<ReleaseManifest> ManifestAsync(string root, string component, CancellationToken cancellationToken = default)
    {
        List<ReleaseFile> entries = [];
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using FileStream stream = File.OpenRead(path);
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            entries.Add(new(relative, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), true));
        }
        string version = FileVersionInfo.GetVersionInfo(Path.Combine(root, AssemblyName(component) + ".dll")).ProductVersion!;
        return new("new", RuntimeInformation.RuntimeIdentifier, Executable(component), entries, ["old"], version);
    }

    /// <summary>対象OSの実行物名を返します。</summary>
    /// <returns>apphostファイル名です。</returns>
    private static string Executable(string component)
    {
        string name = AssemblyName(component);
        if (OperatingSystem.IsWindows())
        {
            name += ".exe";
        }
        return name;
    }

    /// <summary>プロジェクト名に対応する出力アセンブリ名を返します。</summary>
    /// <returns>拡張子を除いた出力名です。</returns>
    private static string AssemblyName(string component)
    {
        const string clientHostComponent = "ClientHost";
        if (string.Equals(component, clientHostComponent, StringComparison.Ordinal))
        {
            return "Clutch-CS.ClientHost";
        }
        return "ReArtWord.Contents.BackgroundService." + component;
    }
}
