namespace ReArtWord.Contents.BackgroundService.Infrastructure.Collaboration;

/// <summary>下書きと編集者表示の保持量を制限します。</summary>
public sealed record CollaborationOptions
{
    /// <summary>通知がなくなった編集者の退場期限です。</summary>
    public TimeSpan PresenceLifetime
    {
        get; init;
    } = TimeSpan.FromSeconds(45);

    /// <summary>参照されなくなった下書きの保持期限です。</summary>
    public TimeSpan DraftLifetime
    {
        get; init;
    } = TimeSpan.FromHours(24);

    /// <summary>保持する下書き数です。</summary>
    public int MaximumDrafts
    {
        get; init;
    } = 64;

    /// <summary>同時表示するUI実体数です。</summary>
    public int MaximumPresence
    {
        get; init;
    } = 512;

    /// <summary>下書き一つで保持する再送判定数です。</summary>
    public int MaximumChanges
    {
        get; init;
    } = 256;

    /// <summary>一編集通知の最大JSONサイズです。</summary>
    public int MaximumDraftBytes
    {
        get; init;
    } = 1048576;
}
