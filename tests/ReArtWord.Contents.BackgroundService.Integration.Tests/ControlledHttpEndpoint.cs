using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実CoreからのHTTP要求を受け、検証側が許可するまで応答を保留します。</summary>
internal sealed class ControlledHttpEndpoint : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private TcpClient? connection;

    /// <summary>本試験だけが使用するループバックの待受を開始します。</summary>
    internal ControlledHttpEndpoint()
    {
        listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/hold";
    }

    /// <summary>実CoreのHTTPノードに設定する接続先です。</summary>
    internal string Url
    {
        get;
    }

    /// <summary>保留中の要求とは別の接続が到着しているかです。</summary>
    internal bool Pending => listener.Pending();

    /// <summary>短いASCII本文を一件受け取り、接続を応答まで保持します。</summary>
    /// <returns>受信した要求本文です。</returns>
    internal async Task<string> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (connection is not null)
        {
            throw new InvalidOperationException("Reply to the current request before accepting another.");
        }
        connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(connection.GetStream(), Encoding.ASCII, false, 1024, true);
        string? line;
        int length = 0;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line.AsSpan(prefix.Length), CultureInfo.InvariantCulture);
            }
        }
        if (length is < 0 or > 1024)
        {
            throw new InvalidDataException("Unexpected fixture request length.");
        }
        char[] body = new char[length];
        if (length != 0 && await reader.ReadBlockAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false) != length)
        {
            throw new EndOfStreamException("The fixture request body is incomplete.");
        }
        return new string(body);
    }

    /// <summary>確認済みの要求へ本文を返し、次の要求を受け付けられるようにします。</summary>
    /// <returns>応答送信と接続の解放です。</returns>
    internal async Task ReplyAsync(string body, CancellationToken cancellationToken = default)
    {
        TcpClient accepted = connection ?? throw new InvalidOperationException("No fixture request is waiting.");
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            byte[] headers = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
            NetworkStream stream = accepted.GetStream();
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accepted.Dispose();
            connection = null;
        }
    }

    /// <summary>失敗時も保留中の接続と待受を閉じます。</summary>
    public void Dispose()
    {
        connection?.Dispose();
        listener.Dispose();
    }
}
