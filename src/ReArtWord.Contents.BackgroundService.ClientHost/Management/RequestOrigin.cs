using System.Net;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>監査対象の変更へ、通信を行うローカルHostの申告情報を付与します。</summary>
internal static class RequestOrigin
{
    /// <summary>認証情報と区別した端末名・任意表示名を既存契約へ設定します。</summary>
    /// <returns>監査対象だけに変更元を付与した引数です。</returns>
    public static NodeValue? Attach(string operation, NodeValue? payload, string? displayName)
    {
        string secret = "secrets.set";
        string logs = "logs.change-options";
        if (!string.Equals(operation, secret, StringComparison.Ordinal) && !string.Equals(operation, logs, StringComparison.Ordinal))
        {
            return payload;
        }
        if (payload?.Kind != EValueKind.Json)
        {
            throw new ArgumentException("変更引数がありません。", nameof(payload));
        }
        if (120 < displayName?.Length)
        {
            throw new ArgumentException("表示名は120文字以内で指定してください。", nameof(displayName));
        }
        ClientAuditOrigin origin = new(Dns.GetHostName(), displayName);
        object request;
        if (string.Equals(operation, secret, StringComparison.Ordinal))
        {
            request = (payload.Json.Deserialize<SecretChangeRequest>(ContractJson.Options) ?? throw new ArgumentException("秘密登録引数がありません。", nameof(payload))) with { Origin = origin };
        }
        else
        {
            request = (payload.Json.Deserialize<LogOptionsChangeRequest>(ContractJson.Options) ?? throw new ArgumentException("ログ設定引数がありません。", nameof(payload))) with { Origin = origin };
        }
        return NodeValue.FromJson(JsonSerializer.SerializeToElement(request, ContractJson.Options));
    }
}
