using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

namespace ReArtWord.Contents.BackgroundService.Nodes.FileSystem.Transfers;

/// <summary>宛先相対パスとsource側の絶対パスを関連付けます。</summary>
/// <param name="Entry">宛先で検証する内容。</param>
/// <param name="SourcePath">source Peerで解決する絶対パス。</param>
internal sealed record FileTransferItem(FileManifestEntry Entry, string SourcePath);
