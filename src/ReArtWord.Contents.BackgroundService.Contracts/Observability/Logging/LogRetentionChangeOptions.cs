namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

/// <summary>保持短縮と既存圧縮ファイルの扱いを明示します。</summary>
/// <param name="ApplyDeletionShorteningToExisting">ApplyDeletionShorteningToExistingの指定値。</param>
/// <param name="ExpandExistingCompressedOnRawExtension">ExpandExistingCompressedOnRawExtensionの指定値。</param>
public sealed record LogRetentionChangeOptions(bool ApplyDeletionShorteningToExisting = false, bool ExpandExistingCompressedOnRawExtension = false);
