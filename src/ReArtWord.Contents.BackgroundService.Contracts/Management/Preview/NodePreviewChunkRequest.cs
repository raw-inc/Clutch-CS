namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>更新で異なる内容が混ざらないよう、取得IDと位置を固定します。</summary>
public sealed record NodePreviewChunkRequest(Guid Id, int Offset, int Length = 32768);
