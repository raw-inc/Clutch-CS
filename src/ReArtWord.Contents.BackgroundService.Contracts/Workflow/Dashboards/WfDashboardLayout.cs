using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

/// <summary>元WFや実行値を持たない、コメントと改行の保存形です。</summary>
public static class WfDashboardLayout
{
    /// <summary>複数行の表示用メモです。</summary>
    public static string CommentType => "dashboard.comment";

    /// <summary>以降のカードを次の段へ送ります。</summary>
    public static string BreakType => "dashboard.break";

    /// <summary>一つのメモが保持する文字数の上限です。</summary>
    public static int MaximumCommentLength => 16384;

    /// <summary>メモ本文を通常のカード設定として保存します。</summary>
    /// <returns>HTMLとして評価しない文字列設定です。</returns>
    public static JsonElement CommentSettings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, MaximumCommentLength);
        return JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = text }, ContractJson.Options);
    }

    /// <summary>改行は表示順以外の値を持ちません。</summary>
    /// <returns>空の設定オブジェクトです。</returns>
    public static JsonElement BreakSettings() => JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal), ContractJson.Options);

    /// <summary>不正な型や上限を超えたメモを黙って切り詰めません。</summary>
    /// <returns>表示・保存できる文字列かです。</returns>
    public static bool TryReadComment(JsonElement settings, out string text)
    {
        text = string.Empty;
        string key = "text";
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        text = value.GetString() ?? string.Empty;
        return text.Length <= MaximumCommentLength;
    }
}
