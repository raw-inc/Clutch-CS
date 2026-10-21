namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>元要求の対象を保持し、別実体の応答で待機を終了させません。</summary>
internal sealed record PendingDatagramReply(WireMessage Request, Guid InstanceId, TaskCompletionSource<WireMessage> Completion)
{
    /// <summary>応答の送信元とクラスタが要求時の対象に一致するか確認します。</summary>
    /// <returns>元の対象からの応答であればtrueです。</returns>
    internal bool Matches(WireMessage response) => response.InstanceId == InstanceId
        && string.Equals(response.SenderId, Request.TargetId, StringComparison.Ordinal)
        && string.Equals(response.ClusterId, Request.ClusterId, StringComparison.Ordinal);
}
