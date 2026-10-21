using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.CoreService.Updates;

/// <summary>更新ペイロードだけを既存の分割ファイルサービスへ接続します。</summary>
internal sealed class UpdateFileReceiver
{
    private readonly Func<UpdatePeerAgent> agent;
    private readonly Lazy<FileTransferService> files;

    /// <summary>通常ワークフローの許可ルートへ更新管理rootを混ぜず転送を分離します。</summary>
    public UpdateFileReceiver(Func<UpdatePeerAgent> agent, UpdatesOptions options)
    {
        this.agent = agent;
        files = new Lazy<FileTransferService>(() => new FileTransferService(new FileSystemOptions([options.StagingRoot ?? throw new InvalidDataException("更新staging rootがありません。")])));
    }

    /// <summary>宣言済みファイルの宛先とハッシュだけで転送を開始します。</summary>
    /// <returns>既存共通ファイルサービスが発行したトークンです。</returns>
    public async Task<FileStageToken> OpenAsync(UpdateFileRequest request, CancellationToken cancellationToken = default)
    {
        LocalUpdateJob job = await agent().ReadJobAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        ReleaseFile file = job.Manifest.Files.Single(item => string.Equals(item.Path, request.RelativePath, StringComparison.Ordinal));
        string destination = Path.Combine(job.PayloadRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        FileAccessPolicy policy = new(new FileSystemOptions([job.PayloadRoot]));
        policy.ValidatePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        policy.ValidatePath(destination);
        string manifestHash = FileTransferManifest.ComputeSha256([new FileManifestEntry(string.Empty, false, file.Size, file.Sha256.ToUpperInvariant())]);
        FileStageRequest stage = new(request.OperationId.ToString("N") + ":" + file.Path, request.SourcePeer, request.SourcePath, destination, manifestHash, false);
        return await files.Value.OpenStageAsync(stage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>既存トークンの単一ファイル内容だけへ分割データを書き込みます。</summary>
    /// <returns>検証済み分割データの保存です。</returns>
    public async Task<bool> WriteAsync(FileChunkWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(request.RelativePath))
        {
            throw new InvalidDataException("更新転送はmanifest内の単一ファイルに限ります。");
        }
        await files.Value.WriteChunkAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>全ファイルハッシュを確認して一ファイルの転送を確定します。</summary>
    /// <returns>共通サービスの実確定結果です。</returns>
    public Task<FileStageCommitResult> CommitAsync(FileStageToken token, CancellationToken cancellationToken = default)
    {
        return files.Value.CommitStageAsync(token, cancellationToken);
    }
}
