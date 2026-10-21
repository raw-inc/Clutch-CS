using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Files;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

/// <summary>coordinator自身への配送は、既存の固定local ジョブを確認してOSコピーで完了します。</summary>
internal static class LocalUpdatePayload
{
    /// <summary>相手が同一Hostの固定ジョブである場合だけ、通信せずペイロードを保存します。</summary>
    /// <returns>自身のペイロードを検証済みならtrue、他Peerならfalseです。</returns>
    internal static async Task<bool> CopyAsync(ClusterUpdateJob cluster, string? clusterJobPath, ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default)
    {
        if (clusterJobPath is null || !string.Equals(peerId, cluster.CoordinatorPeerId, StringComparison.Ordinal))
        {
            return false;
        }
        string jobPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clusterJobPath))!, "job.json");
        LocalUpdateJob local = JsonSerializer.Deserialize<LocalUpdateJob>(await File.ReadAllTextAsync(jobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("同一Hostの更新jobがありません。");
        UpdateLayout layout = new(local.Options, request.OperationId);
        layout.Validate(jobPath);
        string expectedClusterJob = Path.Combine(layout.OperationRoot, "cluster-job.json");
        StringComparison paths = StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            paths = StringComparison.OrdinalIgnoreCase;
        }
        if (local.OperationId != request.OperationId || local.PreviousInstanceId != cluster.PeerInstances[peerId]
            || !string.Equals(local.PeerId, peerId, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(clusterJobPath), expectedClusterJob, paths)
            || !string.Equals(local.PayloadRoot, layout.PayloadRoot, paths)
            || !string.Equals(ContractJson.Hash(local.Manifest), ContractJson.Hash(request.Manifest), StringComparison.Ordinal))
        {
            throw new InvalidDataException("同一Hostの固定jobを確認できません。");
        }
        layout.Validate(layout.PayloadRoot);
        if (Directory.EnumerateFileSystemEntries(layout.PayloadRoot).Any())
        {
            throw new IOException("自身のpayload転送先は空である必要があります。");
        }
        FileAccessPolicy sources = new(new FileSystemOptions([request.PayloadRoot]));
        foreach (ReleaseFile file in request.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = sources.ResolvePath(Path.Combine(request.PayloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            string target = Path.Combine(layout.PayloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            layout.Validate(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            layout.Validate(target);
            File.Copy(source, target, overwrite: false);
            if (file.Executable && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        await ReleaseVerifier.VerifyAsync(request.Manifest, layout.PayloadRoot, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
