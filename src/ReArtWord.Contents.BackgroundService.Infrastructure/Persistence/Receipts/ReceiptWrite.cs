using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>独立したキーと期待版を保った、一件の受付または完了記録です。</summary>
internal sealed record ReceiptWrite(string Scope, string Key, string? ExpectedVersion, NodeValue Value);
