using System.Globalization;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.Nodes.AI;

/// <summary>外部入力を含み得る例外本文を除き、通信障害を識別する安全な情報だけを抽出します。</summary>
internal static class AiFailureDiagnostics
{
    /// <summary>内部例外の通信エラー種別を、本文やスタックなしで記述します。</summary>
    /// <returns>結果の説明へ付加する診断です。</returns>
    internal static string Describe(Exception? error)
    {
        List<string> details = [];
        for (int depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
        {
            details.Add("exception=" + error.GetType().Name);
            if (error is HttpRequestException http)
            {
                details.Add("httpError=" + http.HttpRequestError);
                if (http.StatusCode is { } status)
                {
                    details.Add("httpStatus=" + ((int)status).ToString(CultureInfo.InvariantCulture));
                }
            }
            if (error is SocketException socket)
            {
                details.Add("socketError=" + socket.SocketErrorCode);
                details.Add("nativeError=" + socket.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
            }
        }
        return string.Join("; ", details);
    }
}
