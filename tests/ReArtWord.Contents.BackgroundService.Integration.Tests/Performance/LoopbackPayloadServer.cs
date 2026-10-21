using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>外部APIを叩かず、要求パスごとに識別できる有限のBinary本文を返す試験用HTTPサーバーです。</summary>
internal sealed class LoopbackPayloadServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    private int requests;

    /// <summary>この試験に必要なサイズだけを返す独立した待受を開始します。</summary>
    // 抑制理由: servingはDisposeAsyncで取消後に必ずawaitし、listenerと同じ寿命で所有します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有したHTTP待受をDisposeAsyncで停止してawaitします。")]
    internal LoopbackPayloadServer(int payloadBytes)
    {
        PayloadBytes = payloadBytes;
        listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        serving = ServeAsync(lifetime.Token);
    }

    /// <summary>ループバック上で実際に取得した待受URLです。</summary>
    internal string Url
    {
        get;
    }

    /// <summary>各要求で返す本文長です。</summary>
    internal int PayloadBytes
    {
        get;
    }

    /// <summary>再送や重複処理を検出する実受付回数です。</summary>
    internal int Requests => Volatile.Read(ref requests);

    /// <summary>要求識別子を本文に含め、並列要求間の取り違えを検出します。</summary>
    /// <returns>同じキーで常に一致する試験データです。</returns>
    internal byte[] Payload(string key)
    {
        byte[] bytes = new byte[PayloadBytes];
        Array.Fill(bytes, (byte)0xa5);
        Encoding.UTF8.GetBytes(key).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>本fixtureだけの要求を処理し、全リソースを接続単位で閉じます。</summary>
    /// <returns>取消までの有限応答処理です。</returns>
    private async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
            string request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new IOException("Missing HTTP request.");
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
            }
            string key = request.Split(' ')[1];
            byte[] payload = Payload(key);
            Interlocked.Increment(ref requests);
            byte[] headers = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>待機中の接続受付も取消し、バックグラウンド処理を残しません。</summary>
    /// <returns>所有した待受の終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await serving.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Dispose();
            lifetime.Dispose();
        }
    }
}
