namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>取得IDを再確認できる有限の本文断片です。</summary>
public sealed record NodePreviewChunk(Guid Id, int Offset, ReadOnlyMemory<byte> Data);

