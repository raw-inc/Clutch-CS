using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>認証なしの明示指定と、保存済み秘密情報による既存HTTP認証を区別します。</summary>
internal static class HttpAuthentication
{
    /// <summary>未指定の旧設定では、秘密情報の指定がある場合だけ認証します。</summary>
    /// <returns>秘密情報を取得してヘッダーへ設定する必要があるかです。</returns>
    internal static bool Enabled(NodeSettings settings) => settings.ReadText("authentication", "auto") switch
    {
        "none" => false,
        "secret" => true,
        "auto" => !string.IsNullOrWhiteSpace(settings.ReadText("secretId")),
        _ => throw new FormatException("HTTP authentication mode is invalid."),
    };

    /// <summary>認証を使う要求だけ、秘密情報の参照とヘッダー名を検証します。</summary>
    /// <returns>通信前に訂正できる設定の問題です。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        NodeSettings settings = new(definition.Settings);
        string? mode = settings.ReadText("authentication", "auto");
        bool supported = mode is "auto" or "none" or "secret";
        if (!supported)
        {
            return [new("http.authentication.invalid", "HTTP認証方式を選択してください。", definition.Id, "/authentication")];
        }
        if (!Enabled(settings))
        {
            return [];
        }
        string? secretId = settings.ReadText("secretId");
        if (string.IsNullOrWhiteSpace(secretId))
        {
            return [new("http.secret.required", "認証に使用する保存済み秘密情報を選択してください。", definition.Id, "/secretId")];
        }
        string header = settings.ReadText("secretHeader", "Authorization") ?? string.Empty;
        string scheme = settings.ReadText("secretScheme", "Bearer") ?? string.Empty;
        using HttpRequestMessage probe = new();
        try
        {
            probe.Headers.Add(header, string.Concat(scheme, " placeholder"));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return [new("http.authentication.header.invalid", "認証ヘッダー名または認証方式の接頭辞が不正です。", definition.Id, "/secretHeader")];
        }
        return [];
    }
}
