using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.CoreService.Observability;

/// <summary>秘密値を記録せず、秘密とログ設定の変更対象・元端末・結果を記録します。</summary>
internal static class ConfigurationAudit
{
    /// <summary>保存結果と変更元だけを通常ログへ渡します。</summary>
    /// <returns>本来の処理を阻害しない監査記録です。</returns>
    public static ValueTask WriteAsync(IWorkflowLog log, string peerId, string operation, string target, ClientAuditOrigin? origin, bool saved, string? sensitiveValue = null, CancellationToken cancellationToken = default)
    {
        string level = "ERROR";
        string message = "設定変更の完了を確認できません。";
        if (saved)
        {
            level = "INFO";
            message = "設定変更を保存しました。";
        }
        NodeValue metadata = NodeValue.FromObject(new
        {
            operation, target = Protect(target, sensitiveValue), saved,
            originMachine = Protect(origin?.MachineName, sensitiveValue), displayName = Protect(origin?.DisplayName, sensitiveValue),
        });
        return WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, level, peerId, message, Data: metadata), cancellationToken);
    }

    /// <summary>登録失敗時にも、同じ値を含む参照名や申告情報から秘密を漏らしません。</summary>
    /// <returns>入力した秘密全体を除いた監査項目です。</returns>
    private static string? Protect(string? value, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return value;
        }
        return value?.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }
}
