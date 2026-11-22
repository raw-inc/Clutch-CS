namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>履歴整理に使う起点を識別し、通信で引き継いでも同じ起点から時間を数えます。</summary>
/// <param name="OriginId">最初に起点を作った実体の識別子です。中継先で発行し直しません。</param>
/// <param name="Epoch">起点のUTCです。元の時計の後退を確認した場合だけ、古い方へ補正します。</param>
/// <param name="ElapsedTicks">起点からの経過時間です。引継ぎ後は受信先の単調時計で加算します。</param>
public sealed record HistoryTimeReference(Guid OriginId, DateTimeOffset Epoch, long ElapsedTicks)
{
    /// <summary>保持期間の計算に使用する日時です。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset Time => Epoch.ToUniversalTime().AddTicks(ElapsedTicks);

    /// <summary>不正な起点や経過時間を保持境界へ持ち込みません。</summary>
    public void Validate()
    {
        if (OriginId == Guid.Empty || ElapsedTicks < 0 || (DateTimeOffset.MaxValue - Epoch).Ticks < ElapsedTicks)
        {
            throw new InvalidDataException("履歴整理の基準日時を確認できません。");
        }
    }
}
