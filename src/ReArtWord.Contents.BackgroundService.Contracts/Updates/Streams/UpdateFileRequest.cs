namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

/// <summary>マニフェストで宣言された一ファイルの転送準備です。</summary>
/// <param name="OperationId">固定更新操作です。</param>
/// <param name="RelativePath">マニフェスト内の相対パスです。</param>
/// <param name="SourcePeer">送信元Peerです。</param>
/// <param name="SourcePath">送信元で検証した絶対パスです。</param>
public sealed record UpdateFileRequest(Guid OperationId, string RelativePath, string SourcePeer, string SourcePath);
