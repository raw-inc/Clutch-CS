using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Updater;

/// <summary>初回配置の前提と実参加を既存TOML・UDP境界で確認します。</summary>
internal static class InstallVerification
{
    /// <summary>登録前後の検証結果をJSONと終了コードへ返します。</summary>
    /// <returns>確認成功0、確認失敗1です。</returns>
    public static async Task<int> RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        string stage = "input";
        try
        {
            InstallVerificationRequest request = JsonSerializer.Deserialize<InstallVerificationRequest>(await File.ReadAllTextAsync(requestPath, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException();
            if (!Path.IsPathFullyQualified(request.ConfigurationPath) || !Path.IsPathFullyQualified(request.ManifestPath) || request.TimeoutMilliseconds < 1 || 600000 < request.TimeoutMilliseconds)
            {
                throw new InvalidDataException();
            }
            string configurationText = await File.ReadAllTextAsync(request.ConfigurationPath, cancellationToken).ConfigureAwait(false);
            string manifestText = await File.ReadAllTextAsync(request.ManifestPath, cancellationToken).ConfigureAwait(false);
            string configurationHash = Hash(configurationText);
            string manifestHash = Hash(manifestText);
            stage = "configuration";
            CommonConfiguration configuration = CommonConfigurationReader.Read(configurationText, Path.GetDirectoryName(request.ConfigurationPath)!);
            ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestText, ContractJson.Options) ?? throw new InvalidDataException();
            stage = "platform";
            string[] supported = ["win-x64", "linux-x64", "linux-arm", "linux-arm64", "osx-arm64"];
            if (!supported.Contains(manifest.Platform, StringComparer.Ordinal) || !string.Equals(manifest.Platform, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(manifest.ExpectedBuildVersion))
            {
                throw new InvalidDataException("導入対象の RID または期待するビルド版を確認できません。");
            }
            string root = Path.GetDirectoryName(request.ManifestPath)!;
            stage = "payload";
            await ReleaseVerifier.VerifyAsync(manifest, root, cancellationToken).ConfigureAwait(false);
            stage = "network";
            ClientNetworkBinding binding = Binding(configuration);
            _ = new LocalNetworkBinding(binding.InterfaceId, binding.BindAddress).Resolve();
            stage = "files";
            FileAccessPolicy files = new(configuration.Files);
            foreach (string allowed in configuration.Files.AllowedRoots)
            {
                files.ValidatePath(allowed, true);
            }
            foreach (string workflow in configuration.WorkflowPaths)
            {
                if (!File.Exists(workflow))
                {
                    throw new FileNotFoundException("初期Workflowがありません。");
                }
            }
            if (request.CheckReady)
            {
                stage = "ready";
                if (!string.Equals(request.ConfigurationHash, configurationHash, StringComparison.Ordinal) || !string.Equals(request.ManifestHash, manifestHash, StringComparison.Ordinal) || request.StartedAfter is null || DateTimeOffset.UtcNow < request.StartedAfter)
                {
                    throw new InvalidDataException("登録前と設定またはreleaseが一致しません。");
                }
                await VerifyReadyAsync(request, configuration, manifest, binding, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                stage = "preflight";
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { success = true, stage, configurationHash, manifestHash, startedAfter = DateTimeOffset.UtcNow, peerId = configuration.Cluster.PeerId, clusterId = configuration.Cluster.Id, platform = manifest.Platform, manifest.ExpectedBuildVersion }, ContractJson.Options)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { success = false, stage, reason = "install.verificationFailed" }, ContractJson.Options)).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>同じ参加実体と実OS プロセスが正常参加したことを確認します。</summary>
    /// <returns>期限内の確認完了です。</returns>
    private static async Task VerifyReadyAsync(InstallVerificationRequest request, CommonConfiguration configuration, ReleaseManifest manifest, ClientNetworkBinding binding, CancellationToken cancellationToken = default)
    {
        UpdateObserver observer = new(binding, configuration.Cluster.Id);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.TimeoutMilliseconds);
        Guid? instance = null;
        string entry = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(request.ManifestPath)!, manifest.EntryPoint));
        while (!timeout.IsCancellationRequested)
        {
            PeerInfo peer;
            try
            {
                peer = await observer.FindAsync(configuration.Cluster.PeerId, instance, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                // Observerの一回の発見期限だけでは、配置全体の有限待機を打ち切りません。
                continue;
            }
            instance ??= peer.InstanceId;
            if (!string.Equals(peer.HostId, configuration.Cluster.HostId, StringComparison.Ordinal) || !string.Equals(peer.Address, binding.BindAddress, StringComparison.Ordinal))
            {
                throw new InvalidDataException("導入対象の実HostまたはIPが一致しません。");
            }
            ManagementReply reply = await observer.SendAsync(peer.PeerId, instance.Value, "health", null, true, timeout.Token).ConfigureAwait(false);
            ManagementReply platform = await observer.SendAsync(peer.PeerId, instance.Value, "updates.local.platform", null, true, timeout.Token).ConfigureAwait(false);
            if (reply.Outcome == EOutcome.Succeeded && reply.Value is not null && platform.Outcome == EOutcome.Succeeded && string.Equals(platform.Value?.GetString(), manifest.Platform, StringComparison.Ordinal))
            {
                JsonElement body = reply.Value.Json;
                string readyKey = "ready";
                string instanceKey = "instanceId";
                string peerKey = "peerId";
                string buildKey = "buildVersion";
                if (body.GetProperty(readyKey).ValueKind == JsonValueKind.True && body.GetProperty(instanceKey).GetGuid() == instance && string.Equals(body.GetProperty(peerKey).GetString(), peer.PeerId, StringComparison.Ordinal) && string.Equals(body.GetProperty(buildKey).GetString(), manifest.ExpectedBuildVersion, StringComparison.Ordinal) && MatchesLocalProcess(body, entry, request.StartedAfter!.Value))
                {
                    return;
                }
            }
            await Task.Delay(200, timeout.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("初回配置の通常参加を確認できません。");
    }

    /// <summary>別の保存領域や既存実行物のReadyを導入成功へ流用しません。</summary>
    /// <returns>配置した実行物の今回の起動を確認できたかです。</returns>
    // 抑制理由: BCLがDateTimeで公開するプロセス起動時刻をUTCのDateTimeOffsetへ変換します。
    [SuppressMessage("SourceQuality", "RAW171", Justification = "OSのPID起動時刻を直ちにUTCへ変換し実体を照合します。")]
    private static bool MatchesLocalProcess(JsonElement body, string entry, DateTimeOffset startedAfter)
    {
        using Process process = Process.GetProcessById(body.GetProperty("processId").GetInt32());
        DateTimeOffset actual = new(process.StartTime.ToUniversalTime());
        StringComparison comparison = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        return !process.HasExited && startedAfter <= actual && actual == body.GetProperty("processStartedAt").GetDateTimeOffset() && string.Equals(process.MainModule?.FileName, entry, comparison);
    }

    /// <summary>NIC省略時も設定IPを所有する一つだけへ解決します。</summary>
    /// <returns>暗黙fallbackしない観測先です。</returns>
    private static ClientNetworkBinding Binding(CommonConfiguration configuration)
    {
        string? interfaceId = configuration.Cluster.InterfaceId;
        if (interfaceId is null)
        {
            interfaceId = NetworkInterface.GetAllNetworkInterfaces().Single(network => network.GetIPProperties().UnicastAddresses.Any(address => string.Equals(address.Address.ToString(), configuration.Cluster.BindAddress, StringComparison.Ordinal))).Id;
        }
        return new(interfaceId, configuration.Cluster.BindAddress, configuration.Cluster.BroadcastAddress, configuration.Cluster.Port);
    }

    /// <summary>確認した入力の版を登録後も照合します。</summary>
    /// <returns>UTF8内容のハッシュです。</returns>
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
