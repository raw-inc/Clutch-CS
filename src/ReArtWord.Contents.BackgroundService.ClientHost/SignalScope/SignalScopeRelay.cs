using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>ローカル取得を読み続け、ブラウザーへは一覧と要求された詳細だけを配送します。</summary>
internal static class SignalScopeRelay
{
    /// <summary>どちらかの切断で両方向のI/Oを解放します。</summary>
    /// <returns>中継の終了です。</returns>
    // 抑制理由: finallyで取消後に全タスクの完了をawaitします。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyで両方向のタスクを回収します。")]
    internal static async Task RunAsync(Stream stream, WebSocket socket, SignalScopeTransport? transport = null, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SignalScopeHistory history = new();
        SignalScopeDelivery delivery = new();
        SignalScopeSearchSession searches = new(history, delivery);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable searchLifetime = searches.ConfigureAwait(false);
        Task output = OutputAsync(stream, history, delivery, linked.Token), input = InputAsync(stream, socket, history, delivery, searches, linked.Token);
        Task metadata = MetadataAsync(delivery, transport, linked.Token), writing = delivery.RunAsync(socket, linked.Token);
        try
        {
            await Task.WhenAny(output, input, metadata, writing).ConfigureAwait(false);
        }

        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(output, input, metadata, writing).ConfigureAwait(false);
            }
            finally
            {
                await searches.StopAsync(CancellationToken.None).ConfigureAwait(false);
                history.Clear();
            }
        }
    }

    /// <summary>原本をメモリへ引き取り、低速ブラウザーを取得用pipeへ直結しません。</summary>
    /// <returns>出力方向の終了です。</returns>
    private static async Task OutputAsync(Stream stream, SignalScopeHistory history, SignalScopeDelivery delivery, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[SignalScopeWire.MaximumBytes + 4];
        while (!cancellationToken.IsCancellationRequested)
        {
            int length = await SignalScopeWire.ReadAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (buffer[4] == 2)
            {
                if (history.Add(buffer.AsSpan(4, length)) is { } row)
                {
                    delivery.Row(row);
                }
            }
            else
            {
                JsonObject value = JsonNode.Parse(buffer.AsSpan(5, length - 1)) as JsonObject ?? throw new InvalidDataException("Missing capture control response.");
                string hello = "hello", stats = "stats", state = "state", typeName = "type";
                string? type = value[typeName]?.GetValue<string>();
                if (string.Equals(type, hello, StringComparison.Ordinal))
                {
                    value["protocolVersion"] = 2;
                }
                if (string.Equals(type, stats, StringComparison.Ordinal))
                {
                    value["deliveryDropped"] = delivery.Skipped;
                    (int count, long bytes) = history.Usage;
                    value["retainedCount"] = count;
                    value["retainedBytes"] = bytes;
                }
                byte[] control = SignalScopePacket.Control(value);
                if (string.Equals(type, stats, StringComparison.Ordinal) || string.Equals(type, state, StringComparison.Ordinal))
                {
                    delivery.Notice(string.Equals(type, state, StringComparison.Ordinal) ? SignalScopeDelivery.ENoticeKind.State
                                                                                         : SignalScopeDelivery.ENoticeKind.Statistics, control);
                }
                else
                {
                    await delivery.ControlAsync(control, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>断片化を受けても上限を越えず、元の順序で入力を中継します。</summary>
    /// <returns>入力方向の終了です。</returns>
    private static async Task InputAsync(Stream stream, WebSocket socket, SignalScopeHistory history, SignalScopeDelivery delivery, SignalScopeSearchSession searches, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[65541];
        int offset = 5;
        buffer[4] = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            offset += result.Count;
            if (result.MessageType != WebSocketMessageType.Text || offset == buffer.Length && !result.EndOfMessage)
            {
                throw new InvalidDataException("Invalid capture input frame.");
            }

            if (result.EndOfMessage)
            {
                using JsonDocument json = JsonDocument.Parse(buffer.AsMemory(5, offset - 5));
                SignalScopeRequest request = json.RootElement.Deserialize<SignalScopeRequest>(SignalScopePacket.JsonOptions) ?? throw new InvalidDataException("Missing capture request.");
                try
                {
                    if (await HandleAsync(request, history, delivery, searches, cancellationToken).ConfigureAwait(false))
                    {
                        offset = 5;
                        continue;
                    }
                    string start = "start", exclusions = "exclusions";
                    if (string.Equals(request.Type, start, StringComparison.Ordinal) || string.Equals(request.Type, exclusions, StringComparison.Ordinal))
                    {
                        history.Exclude(json.RootElement);
                    }
                    BinaryPrimitives.WriteInt32BigEndian(buffer, offset - 4);
                    await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
                {
                    await delivery.ControlAsync(SignalScopePacket.Control(new { type = "error", operation = request.Type, request.RequestId, message = "Invalid capture view request or limits." }), cancellationToken).ConfigureAwait(false);
                }
                offset = 5;
            }
        }
    }

    /// <summary>外側のプロキシ経路の変更だけを通知します。</summary>
    /// <returns>接続終了までの監視です。</returns>
    private static async Task MetadataAsync(SignalScopeDelivery delivery, SignalScopeTransport? transport, CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (transport?.Read() is { } update)
            {
                delivery.Notice(SignalScopeDelivery.ENoticeKind.Transport, update);
            }
        }
    }

    /// <summary>履歴・検索は表示接続のメモリ内で完結させます。</summary>
    /// <returns>中継側で処理した要求かです。</returns>
    private static async Task<bool> HandleAsync(SignalScopeRequest request, SignalScopeHistory history, SignalScopeDelivery delivery, SignalScopeSearchSession searches, CancellationToken cancellationToken = default)
    {
        switch (request.Type)
        {
            case "configure":
                history.Configure(request);
                await delivery.ControlAsync(SignalScopePacket.Control(new { type = "configured" }), cancellationToken).ConfigureAwait(false);
                return true;
            case "search":
                await searches.StartAsync(request, cancellationToken).ConfigureAwait(false);
                return true;
            case "detail":
                await delivery.ControlAsync(history.Detail(request), cancellationToken).ConfigureAwait(false);
                return true;
            case "clear":
                await searches.StopAsync(CancellationToken.None).ConfigureAwait(false);
                history.Clear();
                delivery.ClearRows();
                await delivery.ControlAsync(SignalScopePacket.Control(new { type = "cleared" }), cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }
}
