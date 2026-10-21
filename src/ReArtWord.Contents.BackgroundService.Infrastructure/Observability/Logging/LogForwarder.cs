using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>ログ専用TCPで永続受付を確認し、未転送の元ファイルを維持します。</summary>
internal sealed class LogForwarder
{
    private readonly LogStore store;
    private readonly LogRedactor redactor;
    private readonly Func<LogPolicy> options;
    private readonly Func<WorkflowLogEntry, CancellationToken, Task<bool>> accept;
    private readonly Action<string, string?> failure;
    private string? endpoint;

    /// <summary>保持済みファイルと宛先受付を接続します。</summary>
    internal LogForwarder(LogStore store, LogRedactor redactor, Func<LogPolicy> options, Func<WorkflowLogEntry, CancellationToken, Task<bool>> accept, Action<string, string?> failure)
    {
        this.store = store;
        this.redactor = redactor;
        this.options = options;
        this.accept = accept;
        this.failure = failure;
    }

    /// <summary>実際にbindできた待受endpointです。</summary>
    internal string? Endpoint => Volatile.Read(ref endpoint);

    /// <summary>設定した各宛先への未転送バイト数です。</summary>
    internal long PendingBytes
    {
        get
        {
            LogOptions current = options().Options;
            IReadOnlyList<LogForwardTarget> targets = current.ForwardTargets ?? [];
            return store.Segments.Where(segment => segment.LocalOrigin).Sum(segment => segment.Bytes * targets.Count(target => !string.Equals(target.PeerId, current.PeerId, StringComparison.Ordinal) && !segment.ForwardedTo.Contains(target.PeerId, StringComparer.Ordinal)));
        }
    }

    /// <summary>同NIC同IPを維持して待受を回復します。</summary>
    /// <returns>終了要求までの待受です。</returns>
    internal async Task ListenAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LogOptions current = options().Options;
            try
            {
                if (current.ListenPort is not int port)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                LocalNetworkBinding binding = Binding(current);
                using NetworkBindingLease lease = new(binding, cancellationToken);
                using TcpListener listener = new(binding.Resolve(), port);
                listener.Start();
                Volatile.Write(ref endpoint, listener.LocalEndpoint.ToString());
                failure("listen", null);
                while (!lease.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested && SameListener(current, options().Options))
                {
                    try
                    {
                        using TcpClient? client = await AcceptAsync(listener, lease.Token).ConfigureAwait(false);
                        if (client is not null)
                        {
                            await ReceiveAsync(client, current, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failure("receive", "log.receive.unavailable");
                    }
                    catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or SocketException or JsonException or FormatException or InvalidOperationException)
                    {
                        failure("receive", "log.receive.unavailable");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure("listen", "log.listen.unavailable");
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or SocketException or ArgumentException or InvalidOperationException)
            {
                failure("listen", "log.listen.unavailable");
            }
            finally
            {
                Volatile.Write(ref endpoint, null);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>設定確認のため接続待ちだけに短い期限を設けます。</summary>
    /// <returns>受理した接続、または空待機です。</returns>
    private static async Task<TcpClient?> AcceptAsync(TcpListener listener, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        poll.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            return await listener.AcceptTcpClientAsync(poll.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>一回の通信では暗黙再送せず、次の復旧周期へ未転送を残します。</summary>
    /// <returns>一周期の転送試行です。</returns>
    internal async Task ForwardAsync(CancellationToken cancellationToken = default)
    {
        LogOptions current = options().Options;
        bool failed = false;
        foreach (LogForwardTarget target in current.ForwardTargets ?? [])
        {
            if (string.Equals(target.PeerId, current.PeerId, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (LogSegment segment in store.Segments.Where(segment => segment.LocalOrigin && !segment.ForwardedTo.Contains(target.PeerId, StringComparer.Ordinal)).OrderBy(segment => segment.Date).Take(16))
            {
                try
                {
                    IReadOnlyList<string> patterns = await redactor.ReadPatternsAsync(cancellationToken).ConfigureAwait(false);
                    List<WorkflowLogEntry> entries = [];
                    await foreach (WorkflowLogEntry entry in store.ReadAsync(segment, cancellationToken).ConfigureAwait(false))
                    {
                        entries.Add(LogRedactor.Redact(entry, patterns));
                    }
                    await SendAsync(current, target, entries, cancellationToken).ConfigureAwait(false);
                    await store.MarkForwardedAsync(segment.Id, target.PeerId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or SocketException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
                {
                    failed = true;
                    failure("forward", "log.forward.unavailable");
                    break;
                }
            }
        }
        if (!failed)
        {
            failure("forward", null);
        }
    }

    /// <summary>一接続でbounded フレームを送り、保存済みACKだけを認めます。</summary>
    /// <returns>宛先保存の確認です。</returns>
    private static async Task SendAsync(LogOptions options, LogForwardTarget target, IReadOnlyList<WorkflowLogEntry> entries, CancellationToken cancellationToken = default)
    {
        LocalNetworkBinding binding = Binding(options);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using TcpClient client = new(new IPEndPoint(binding.Resolve(), 0));
        await client.ConnectAsync(target.RemoteAddress, target.Port, deadline.Token).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new LogTransferPacket(options.PeerId, target.PeerId, entries));
        await TcpFrame.WriteAsync(stream, payload, "lengthPrefix", deadline.Token).ConfigureAwait(false);
        byte[]? response = await TcpFrame.ReadAsync(stream, "lengthPrefix", 65536, deadline.Token).ConfigureAwait(false);
        LogTransferAck? acknowledgement = null;
        if (response is not null)
        {
            acknowledgement = JsonSerializer.Deserialize<LogTransferAck>(response);
        }
        if (acknowledgement is null || !acknowledgement.Accepted || !string.Equals(acknowledgement.PeerId, target.PeerId, StringComparison.Ordinal))
        {
            throw new IOException("ログ転送先の永続受付を確認できません。");
        }
    }

    /// <summary>受信一件は待受設定更新へ遡及させません。</summary>
    /// <returns>ACK送信の完了です。</returns>
    private async Task ReceiveAsync(TcpClient client, LogOptions options, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using NetworkStream stream = client.GetStream();
        byte[]? bytes = await TcpFrame.ReadAsync(stream, "lengthPrefix", options.QueueBytes + 65536, deadline.Token).ConfigureAwait(false);
        LogTransferPacket? packet = null;
        if (bytes is not null)
        {
            packet = JsonSerializer.Deserialize<LogTransferPacket>(bytes);
        }
        bool accepted = packet is { Entries: not null } && string.Equals(packet.TargetPeer, options.PeerId, StringComparison.Ordinal)
            && !string.Equals(packet.SenderPeer, options.PeerId, StringComparison.Ordinal) && packet.Entries.Count <= 64;
        if (accepted && packet is { Entries: not null })
        {
            foreach (WorkflowLogEntry entry in packet.Entries)
            {
                accepted &= await accept(entry, deadline.Token).ConfigureAwait(false);
            }
        }
        await TcpFrame.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new LogTransferAck(options.PeerId, accepted)), "lengthPrefix", deadline.Token).ConfigureAwait(false);
        if (accepted)
        {
            failure("receive", null);
        }
    }

    /// <summary>NICとIPv4を省略せず検証します。</summary>
    /// <returns>同じインターフェースとIPだけを解決するbindingです。</returns>
    private static LocalNetworkBinding Binding(LogOptions options)
    {
        return new(options.InterfaceId ?? string.Empty, options.LocalAddress ?? string.Empty);
    }

    /// <summary>既存接続を完了してから次のbindingへ切り替えます。</summary>
    /// <returns>同じlistenerを継続できるかです。</returns>
    private static bool SameListener(LogOptions first, LogOptions second)
    {
        return first.ListenPort == second.ListenPort && string.Equals(first.InterfaceId, second.InterfaceId, StringComparison.Ordinal) && string.Equals(first.LocalAddress, second.LocalAddress, StringComparison.Ordinal);
    }
}
