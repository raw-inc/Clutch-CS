using ReArtWord.Contents.BackgroundService.Contracts.Retention;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>すでに実施した保持境界です。設定を延長しても削除済み範囲は巻き戻しません。</summary>
/// <param name="RawBefore">種類ごとの非圧縮区間の下限です。</param>
/// <param name="DeletedBefore">種類ごとの保存区間の下限です。</param>
/// <param name="OperationBefore">未知の時刻付き操作を新規受付しない境界です。</param>
/// <param name="ObservedAt">この基準点が確認した共有状態の日時です。</param>
public sealed record HistoryRetentionBoundary(IReadOnlyDictionary<ERetentionCategory, DateTimeOffset> RawBefore,
    IReadOnlyDictionary<ERetentionCategory, DateTimeOffset> DeletedBefore, DateTimeOffset OperationBefore, DateTimeOffset ObservedAt);
