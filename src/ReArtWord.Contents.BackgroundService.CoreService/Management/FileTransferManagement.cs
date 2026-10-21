using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>GUI転送を既存のファイル許可・排他・段階保存へ接続します。</summary>
internal static class FileTransferManagement
{
    /// <summary>同じ実サービスを使い、コピー内容は検証済みの段階からだけ確定します。</summary>
    internal static void Register(ManagementRegistry registry, FileTransferService files)
    {
        registry.Read("transfer.stat", async (value, token) => await files.StatAsync(ManagementRegistry.Id(value), token).ConfigureAwait(false));
        registry.Read("transfer.list", async (value, token) =>
        {
            FileTransferListRequest request = ManagementRegistry.Body<FileTransferListRequest>(value);
            return await files.ListAsync(request.Path, request.Continuation, token).ConfigureAwait(false);
        });
        registry.Read("transfer.read", async (value, token) => await files.ReadChunkAsync(ManagementRegistry.Body<FileChunkRequest>(value), token).ConfigureAwait(false));
        registry.Transient("transfer.open", async (value, token) => await files.OpenStageAsync(ManagementRegistry.Body<FileStageRequest>(value), token).ConfigureAwait(false));
        registry.Transient("transfer.directory", async (value, token) =>
        {
            await files.StageDirectoryAsync(ManagementRegistry.Body<FileStageDirectoryRequest>(value), token).ConfigureAwait(false);
            return true;
        });
        registry.Transient("transfer.write", async (value, token) =>
        {
            await files.WriteChunkAsync(ManagementRegistry.Body<FileChunkWriteRequest>(value), token).ConfigureAwait(false);
            return true;
        });
        registry.Write("transfer.commit", async (value, token) => await files.CommitStageAsync(ManagementRegistry.Body<FileStageToken>(value), token).ConfigureAwait(false));
        registry.Transient("transfer.discard", async (value, token) =>
        {
            await files.DiscardStageAsync(ManagementRegistry.Body<FileStageToken>(value), token).ConfigureAwait(false);
            return true;
        });
    }
}
