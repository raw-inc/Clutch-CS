using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>開始済み処理を再実行しないための永続記録です。</summary>
/// <param name="Fingerprint">要求内容。</param>
/// <param name="InstanceId">受理した実体。</param>
/// <param name="ExpiresAt">最初の開始期限。</param>
/// <param name="Response">完了結果。nullなら開始済みで結果未確認。</param>
/// <param name="SavedAt">保存先PCで受付または完了を記録した日時です。新形式の保持期限に使い、要求元のExpiresAtと比較しません。</param>
internal sealed record RequestReceipt(string Fingerprint, Guid InstanceId, DateTimeOffset ExpiresAt, ClusterResponse? Response,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SavedAt = null)
{
    /// <summary>開始期限は変えず、新形式の保持期間だけを完了記録の日時から数えます。</summary>
    /// <returns>元の指紋・実体・開始期限を引き継いだ完了記録です。</returns>
    internal RequestReceipt Complete(ClusterResponse response, DateTimeOffset now)
    {
        DateTimeOffset? savedAt = SavedAt is null ? null
                                                  : now;
        return this with { Response = response, SavedAt = savedAt };
    }
}
