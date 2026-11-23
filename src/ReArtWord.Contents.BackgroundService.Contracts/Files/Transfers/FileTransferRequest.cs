using System.Text.Json.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;

/// <summary>キューへ一度だけ追加するコピー要求です。</summary>
/// <param name="Id">画面で固定したジョブIDです。</param>
/// <param name="Source">コピー元です。</param>
/// <param name="Destination">コピー先です。</param>
/// <param name="SourcePath">元ファイルまたはディレクトリです。</param>
/// <param name="DestinationDirectory">コピーを入れる既存ディレクトリです。</param>
/// <param name="Overwrite">同名の宛先ファイルを、検証したコピーで置き換えるかどうかです。</param>
public sealed record FileTransferRequest(Guid Id, FileTransferPeer Source, FileTransferPeer Destination, string SourcePath, string DestinationDirectory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Overwrite = false);
