using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Contracts.Collaboration;

/// <summary>実受信元と表示期限を持つ編集者です。</summary>
public sealed record PresenceView(string PeerId, Guid PeerInstanceId, PresenceUpdate Presence, DateTimeOffset ExpiresAt)
{
    /// <summary>送信元Coreの受付連番です。同じCore実体・UI実体の通知順を比較します。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Sequence
    {
        get; init;
    }

    /// <summary>応答を作成した時点の残り表示時間です。中継先では通信・受信後の経過時間を差し引きます。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RemainingTicks
    {
        get; init;
    }

    /// <summary>残り時間を受け取ってからの計測です。日時は表示用に残します。</summary>
    [JsonIgnore]
    public ObservationAge? ResponseAge
    {
        get; init;
    }

    /// <summary>再読込みの間も減り続ける残り表示時間です。旧形式では確認できません。</summary>
    [JsonIgnore]
    public TimeSpan? Remaining => RemainingTicks is { } ticks ? TimeSpan.FromTicks(Math.Max(0, ticks - (ResponseAge?.Elapsed.Ticks ?? 0)))
                                                              : null;

    /// <summary>対応する通知は経過時間で判定し、旧形式だけ従来の日時比較を使います。</summary>
    /// <returns>参加表示の期間内かです。編集権や草稿の保存状態は表しません。</returns>
    public bool IsCurrent(DateTimeOffset now) => Remaining is { } remaining ? TimeSpan.Zero < remaining
                                                                            : now < ExpiresAt;
}
