using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

/// <summary>設定操作全体の段階結果と開始条件を保持します。</summary>
/// <param name="Fingerprint">要求内容。</param>
/// <param name="StartedAt">最初の開始時刻。</param>
/// <param name="Context">固定参加集合等。</param>
/// <param name="Result">完了結果。nullは未確認。</param>
internal sealed record ConfigurationOperation(string Fingerprint, DateTimeOffset StartedAt, JsonElement? Context, JsonElement? Result)
{
    /// <summary>保存したContextの形式から読み取った生成方式です。旧操作はLegacyのまま回復します。</summary>
    [JsonIgnore]
    internal EDerivedIdFormat IdentityFormat
    {
        get; init;
    }
}
