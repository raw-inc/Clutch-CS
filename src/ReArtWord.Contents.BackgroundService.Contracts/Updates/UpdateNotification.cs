using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Contracts.Updates;

/// <summary>更新本体の再実行とは分けて配送する、内容識別付きの通知です。</summary>
/// <param name="Id">同じ事象内容で安定したIDです。</param>
/// <param name="Name">共通Hookの事象名です。</param>
/// <param name="ObservedAt">事象を確認した時刻です。</param>
/// <param name="Data">パス・接続秘密値を除いた結果です。</param>
public sealed record UpdateNotification(Guid Id, string Name, DateTimeOffset ObservedAt, UpdateEventData Data)
{
    /// <summary>更新開始時の方式を、ClientHostからCoreへの配送にも引き継ぎます。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EDerivedIdFormat IdentityFormat
    {
        get; init;
    }

    /// <summary>配送時刻・配送先を変更しても同じ事象を増やしません。</summary>
    /// <returns>内容から識別できる通知です。</returns>
    public static UpdateNotification Create(string name, UpdateEventData data, DateTimeOffset observedAt, EDerivedIdFormat format = EDerivedIdFormat.Legacy)
        => new(DerivedOperationIdentity.FromHash(Convert.FromHexString(ContractJson.Hash(new { name, data })), format), name, observedAt, data) { IdentityFormat = format };
}
