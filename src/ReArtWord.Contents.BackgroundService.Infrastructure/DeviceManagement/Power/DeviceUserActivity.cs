namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement.Power;

/// <summary>時刻計算の揺らぎとは独立した入力識別子を持つ確認結果です。</summary>
public sealed record DeviceUserActivity(int SessionId, DateTimeOffset LastActivity, string Identity)
{
    /// <summary>OSの入力tickから確認した無操作時間です。WTSの日時だけでは取得できません。</summary>
    public TimeSpan? IdleFor
    {
        get; init;
    }

    /// <summary>セッションの接続・ログオン・入力を区別する観測値です。既存の停止受付IDは維持します。</summary>
    public string? InputSample
    {
        get; init;
    }
}
