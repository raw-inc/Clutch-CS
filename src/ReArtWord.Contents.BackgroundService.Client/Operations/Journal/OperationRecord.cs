using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Journal;

/// <summary>再照会に必要な開始時の要求と直近の実行結果です。</summary>
internal sealed record OperationRecord(Guid OperationId, string Kind, string ReadPath, string? RecoverPath, JsonElement Request, DateTimeOffset CreatedAt, JsonElement? Result = null, string? PeerId = null, string? ClusterId = null);
