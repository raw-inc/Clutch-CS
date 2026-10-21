using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Http;

/// <summary>例外本文を公開せず、HTTP失敗の段階と利用者が訂正できる原因を返します。</summary>
internal static class HttpRequestFailure
{
    /// <summary>開始前の失敗と、送信後に結果が確認できない要求を区別します。</summary>
    /// <returns>秘密値やURL本文を含めない、一回の要求の結果です。</returns>
    internal static NodeResult From(Exception exception, bool dispatched, bool responseReceived, bool cancelled)
    {
        EOutcome outcome = dispatched ? EOutcome.Unknown
                                      : EOutcome.NotExecuted;
        if (responseReceived && exception is FormatException or JsonException or ArgumentException)
        {
            return NodeResult.Failure("http.response.invalid", "HTTP応答の形式が不正、または本文が受信上限を超えています。");
        }
        if (exception is OperationCanceledException)
        {
            return cancelled ? NodeResult.Failure("http.request.cancelled", "HTTP要求は取り消されました。送信開始後の相手側の結果は確認できません。", outcome)
                             : NodeResult.Failure("http.request.timeout", "HTTP要求が制限時間内に完了しませんでした。接続先・通信経路・タイムアウト設定を確認してください。", outcome);
        }
        Exception? cause = exception;
        while (cause is not null)
        {
            if (cause is SocketException socket)
            {
                (string Code, string Message)? failure = socket.SocketErrorCode switch
                {
                    SocketError.NetworkDown => ("http.network.down", "指定したNICが停止中です。実行PCで稼働中のNICと、そのNICのIPv4を選択してください。"),
                    SocketError.AddressNotAvailable => ("http.network.addressUnavailable", "指定したNICまたはIPv4が実行PCにありません。NICとローカルIPv4を再選択してください。"),
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ("http.dns.failed", "接続先ホスト名をIPv4へ解決できません。URLと実行PCのDNS・VPN接続を確認してください。"),
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => ("http.network.unreachable", "指定NICから接続先へ到達できません。実行PCのルート・VPN・接続先を確認してください。"),
                    SocketError.ConnectionRefused => ("http.connection.refused", "接続先がHTTP接続を拒否しました。接続先のポートとサービスの稼働状態を確認してください。"),
                    _ => null,
                };
                if (failure is { } known)
                {
                    return NodeResult.Failure(known.Code, known.Message, outcome);
                }
            }
            cause = cause.InnerException;
        }
        return NodeResult.Failure("http.request.unconfirmed", "HTTP処理の完了を確認できません。", outcome);
    }
}
