using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>要求の受付・完了にはCASを維持し、未対応媒体では既存の耐久取引を使います。</summary>
internal static class RequestReceiptPersistence
{
    /// <summary>通常データの操作履歴を省略する用途には使用しません。</summary>
    internal static void Validate(string scope, string key, NodeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string prefix = "receipts:", format = "N";
        if (!scope.StartsWith(prefix, StringComparison.Ordinal) || scope.Length == prefix.Length || !Guid.TryParseExact(key, format, out _))
        {
            throw new ArgumentException("要求受付の保存先が不正です。", nameof(scope));
        }
    }

    /// <summary>確定を確認できなければ呼出元へ伝え、副作用を開始・再実行させません。</summary>
    /// <returns>耐久保存済みの現在値です。</returns>
    internal static async ValueTask<DurableEntry?> WriteAsync(IDurableRepository repository, string scope, string key, string? expectedVersion, NodeValue value, CancellationToken cancellationToken = default)
    {
        Validate(scope, key, value);
        if (repository is IRequestReceiptStore store)
        {
            return await store.CompareExchangeReceiptAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
        DurableReceipt result = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new(scope, key, expectedVersion, value)], cancellationToken).ConfigureAwait(false);
        return result.Outcome == EOutcome.Succeeded ? result.Entries.Single()
                                                    : null;
    }
}
