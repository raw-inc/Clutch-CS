using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Terminal;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>表示接続だけを所有し、切断時にもPTYを終了させません。</summary>
internal sealed class TerminalConnection : IAsyncDisposable
{
    // 抑制理由: 共通のCloseAsyncが完了待ちと全資源の破棄を所有します。
    [SuppressMessage("Usage", "CA2213", Justification = "SessionFailureBoundary.CloseAsyncがfinallyでlistenerを破棄します。")]
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32), header = new byte[4];

    // 抑制理由: 通信の完了後、共通のCloseAsyncへ渡して破棄します。
    [SuppressMessage("Usage", "CA2213", Justification = "SessionFailureBoundary.CloseAsyncが通信完了後のfinallyでsendingを破棄します。")]
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly Channel<long> consumed = Channel.CreateBounded<long>(1);
    private readonly TerminalSession session;
    private readonly TerminalAttachRequest request;
    private readonly Task completion;
    private long pendingOffset = -1;
    private readonly SessionFailureBoundary failures;

    /// <summary>同じ開始要求で受付を増やさず、明示IPで一回だけ待ち受けます。</summary>
    // 抑制理由: completionを保持し、DisposeAsyncで取消しと完了を確認します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "completionの寿命を接続自身が所有します。")]
    internal TerminalConnection(string address, TerminalSession session, TerminalAttachRequest request, SessionFailureBoundary failures)
    {
        this.failures = failures;
        this.session = session;
        this.request = request;
        listener = new(IPAddress.Parse(address), 0);
        bool listening = false;
        try
        {
            listener.Start(1);
            listening = true;
        }
        finally
        {
            if (!listening)
            {
                listener.Dispose(); lifetime.Dispose(); sending.Dispose(); CryptographicOperations.ZeroMemory(token);
            }
        }

        Ticket = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(token), DateTimeOffset.UtcNow.AddSeconds(15));
        completion = failures.RunAsync(() => RunAsync(lifetime.Token), "Terminal", Id, "connection", lifetime.Token);
    }

    /// <summary>一接続の開始識別子です。</summary>
    internal Guid Id => request.ConnectionId;

    /// <summary>管理輸送へ返す一回限りの許可です。</summary>
    internal TerminalTicket Ticket
    {
        get;
    }

    /// <summary>通信だけを停止し、所有タスクと資源を回収します。</summary>
    /// <returns>両方向の通信終了です。</returns>
    // 抑制理由: IAsyncDisposable固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await failures.CloseAsync("Terminal", Id, listener, lifetime, completion, token, sending).ConfigureAwait(false);
    }

    /// <summary>所有者に結び付いたtokenを確認し、出力と入力を接続します。</summary>
    /// <returns>表示接続の寿命です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            admission.CancelAfter(TimeSpan.FromSeconds(15));
            using TcpClient client = await listener.AcceptTcpClientAsync(admission.Token).ConfigureAwait(false);
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            byte[] supplied = new byte[32];
            await stream.ReadExactlyAsync(supplied, admission.Token).ConfigureAwait(false);
            listener.Stop();
            if (!CryptographicOperations.FixedTimeEquals(supplied, token) || session.View.Owner != request.Owner)
            {
                throw new InvalidDataException("Terminal ticket was rejected.");
            }

            await RelayAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        finally
        {
            listener.Stop();
        }
    }

    /// <summary>片方向の終了で両方向を止め、未完了I/Oを残しません。</summary>
    /// <returns>中継完了です。</returns>
    // 抑制理由: finallyで取消後に全タスクの完了をawaitします。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyで両方向のタスクを回収します。")]
    private async Task RelayAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using Task output = OutputAsync(stream, linked.Token);
        using Task input = InputAsync(stream, linked.Token);
        try
        {
            await (await Task.WhenAny(output, input).ConfigureAwait(false)).ConfigureAwait(false);
        }

        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SessionRelay.DrainAsync(Task.WhenAll(output, input), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>履歴の区切りを固定し、描画完了で逆圧をかけて変更時だけ送ります。</summary>
    /// <returns>出力または所有者変更の通知完了です。</returns>
    private async Task OutputAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        long boundary = session.OutputEnd, offset = 0;
        bool ready = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            long ceiling = long.MaxValue;
            if (!ready)
            {
                ceiling = boundary;
            }
            TerminalOutput value = session.Read(offset, 65536, ceiling);
            if (value.Session.Owner != request.Owner)
            {
                await WriteAsync(stream, new
                {
                    type = "closed",
                    reason = "ownership",
                    session = value.Session
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (value.Offset < value.NextOffset)
            {
                Interlocked.Exchange(ref pendingOffset, value.NextOffset);
                await WriteAsync(stream, new
                {
                    type = "output",
                    output = value,
                    history = !ready
                }, cancellationToken).ConfigureAwait(false);
                _ = await consumed.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                offset = value.NextOffset;
                continue;
            }

            offset = value.NextOffset;
            if (!ready)
            {
                ready = true;
                await WriteAsync(stream, new
                {
                    type = "ready",
                    session = value.Session
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (value.Session.Exited)
            {
                await WriteAsync(stream, new
                {
                    type = "closed",
                    reason = "exited",
                    session = value.Session
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await session.WaitAsync(offset, request.Owner, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>描画位置と連番入力を検証し、入力の受理を通知します。</summary>
    /// <returns>入力接続の終了です。</returns>
    private async Task InputAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[16384];
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = await TerminalWire.ReadOrEndAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return;
            }
            TerminalStreamInput message = JsonSerializer.Deserialize<TerminalStreamInput>(buffer.AsSpan(4, length), ContractJson.Options) ?? throw new InvalidDataException("Terminal input is missing.");
            switch (message.Type)
            {
                case "consumed":
                    if (message.Offset < 0 || Interlocked.CompareExchange(ref pendingOffset, -1, message.Offset) != message.Offset || !consumed.Writer.TryWrite(message.Offset))
                    {
                        throw new InvalidDataException("Invalid terminal output acknowledgment.");
                    }

                    break;
                case "input":
                    TerminalRequest value = message.Request ?? throw new InvalidDataException("Terminal request is missing.");
                    if (value.SessionId != request.SessionId || value.Owner != request.Owner)
                    {
                        throw new InvalidDataException("Terminal ownership changed.");
                    }

                    TerminalView view = await session.InputAsync(value, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(stream, new
                    {
                        type = "ack",
                        session = view
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                default: throw new InvalidDataException("Unknown terminal message.");
            }
        }
    }

    /// <summary>出力と入力確認を同じ順序付き接続へ直列に書きます。</summary>
    /// <returns>一フレームの送信完了です。</returns>
    private async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (TerminalWire.MaximumMessageBytes < bytes.Length)
        {
            throw new InvalidDataException("Terminal output exceeded its limit.");
        }

        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        finally
        {
            sending.Release();
        }
    }
}

