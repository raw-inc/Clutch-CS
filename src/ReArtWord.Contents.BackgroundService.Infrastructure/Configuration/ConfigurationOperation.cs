using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

/// <summary>設定操作全体の段階結果と開始条件を保持します。</summary>
/// <param name="Fingerprint">要求内容。</param>
/// <param name="StartedAt">最初の開始時刻。</param>
/// <param name="Context">固定参加集合等。</param>
/// <param name="Result">完了結果。nullは未確認。</param>
internal sealed record ConfigurationOperation(string Fingerprint, DateTimeOffset StartedAt, JsonElement? Context, JsonElement? Result);
