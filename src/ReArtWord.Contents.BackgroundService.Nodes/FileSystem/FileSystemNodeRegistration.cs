using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem;

/// <summary>共通許可範囲と同じサービスを使うファイル能力を登録します。</summary>
public static class FileSystemNodeRegistration
{
    /// <summary>全ファイル操作で競合と補償を共有します。</summary>
    /// <returns>登録するファイル能力です。</returns>
    public static IReadOnlyList<INode> Create(FileSystemOptions options, IPeerFileTransfer? peerTransfer = null, FileTransferService? transferService = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        FileTransferService service = transferService ?? new(options);
        string[] operations = ["file.read", "file.create", "file.replace", "file.upsert", "file.append", "file.copy", "file.move", "file.rename", "file.delete", "file.exists", "directory.create", "directory.exists", "directory.list", "directory.copy", "directory.move", "directory.rename", "directory.delete"];
        return [.. operations.Select(operation => (INode)new FileActionNode(operation, service.AccessPolicy, service.Operations, service.Snapshots, options)), new FileWatchNode(service.AccessPolicy), new FileTransferNode(service, peerTransfer)];
    }
}
