using ReArtWord.Contents.BackgroundService.Contracts.Collaboration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>新しい編集とその統合に自動保存IDを付け、通知順やCoordinatorの交代では作り直しません。</summary>
internal static class DraftAutoApplyIdentity
{
    /// <summary>内容が変わらない再通知では元IDを保持し、明示的な編集にだけ新しいIDを割り当てます。</summary>
    /// <returns>下書きと一緒に共有・保存する操作IDです。</returns>
    internal static Guid? Change(DraftView previous, string revision, Guid edit)
        => string.Equals(previous.SessionVersion, revision, StringComparison.Ordinal) ? previous.AutoApplyOperationId
                                                                                      : Create(new { edit, revision });

    /// <summary>一方の版を採用した場合はそのIDを保持し、新旧の下書きを通知順で別の操作にしません。</summary>
    /// <returns>統合結果に固定する操作IDです。</returns>
    internal static Guid? Merge(DraftView left, DraftView right, string revision)
    {
        bool sameLeft = string.Equals(left.SessionVersion, revision, StringComparison.Ordinal);
        bool sameRight = string.Equals(right.SessionVersion, revision, StringComparison.Ordinal);
        if (sameLeft && sameRight)
        {
            if (left.AutoApplyOperationId is null || right.AutoApplyOperationId is null)
            {
                return null;
            }
            return new[] { left.AutoApplyOperationId.Value, right.AutoApplyOperationId.Value }.Order().First();
        }
        if (sameLeft)
        {
            return left.AutoApplyOperationId;
        }
        if (sameRight)
        {
            return right.AutoApplyOperationId;
        }
        if (left.AutoApplyOperationId is null && right.AutoApplyOperationId is null)
        {
            return null;
        }
        Guid?[] parents = [left.AutoApplyOperationId, right.AutoApplyOperationId];
        return Create(new { parents = parents.Order().ToArray(), revision });
    }

    /// <summary>新世代の編集内容から、日時を含まないIDを作ります。</summary>
    /// <returns>UUIDv8形式の内容識別子です。</returns>
    private static Guid Create<T>(T value) => DerivedOperationIdentity.FromHash(Convert.FromHexString(ContractJson.Hash(value)), EDerivedIdFormat.Uuid8);
}
