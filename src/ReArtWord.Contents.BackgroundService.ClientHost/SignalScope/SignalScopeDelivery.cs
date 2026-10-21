using System.Net.WebSockets;
using System.Threading.Channels;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>低速ブラウザーと取得側の読取りを分離し、未送信一覧を有界に保ちます。</summary>
internal sealed class SignalScopeDelivery
{
    private readonly Channel<byte[]> controls = Channel.CreateBounded<byte[]>(32);
    private readonly Channel<byte[]> rows;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly byte[]?[] notices = new byte[]?[3];
    private long skipped;

    /// <summary>一覧は最新の256通知を優先し、古い未送信行を破棄します。</summary>
    internal SignalScopeDelivery() => rows = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true,
    }, _ => Interlocked.Increment(ref skipped));

    /// <summary>回線側へ送り切れなかった一覧通知数です。取得欠落とは区別します。</summary>
    internal long Skipped => Interlocked.Read(ref skipped);

    /// <summary>更新可能な定期通知の固定スロットです。</summary>
    internal enum ENoticeKind
    {
        State,
        Statistics,
        Transport,
    }

    /// <summary>状態と統計は最新の一件へ集約し、低速回線で取得の読取りを待たせません。</summary>
    internal void Notice(ENoticeKind kind, byte[] frame)
    {
        Volatile.Write(ref notices[(int)kind], frame);
        wake.Writer.TryWrite(true);
    }

    /// <summary>更新された状態を一件だけ取り出します。</summary>
    /// <returns>通知があるかです。</returns>
    private bool TryNotice(out byte[]? frame)
    {
        for (int index = 0; index < notices.Length; index++)
        {
            frame = Interlocked.Exchange(ref notices[index], null);
            if (frame is not null)
            {
                return true;
            }
        }
        frame = null;
        return false;
    }

    /// <summary>取得側を待たせず、軽い一覧フレームだけを配送待ちへ入れます。</summary>
    internal void Row(byte[] frame)
    {
        rows.Writer.TryWrite(frame);
        wake.Writer.TryWrite(true);
    }

    /// <summary>状態・操作応答は一覧より優先して有限キューへ入れます。</summary>
    /// <returns>配送キューへの追加です。</returns>
    internal async Task ControlAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        await controls.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        wake.Writer.TryWrite(true);
    }

    /// <summary>明示クリアで未送信の一覧を延命しません。</summary>
    internal void ClearRows()
    {
        while (rows.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>一つの書込みだけを実行し、完全に応答しない接続は終了させます。</summary>
    /// <returns>接続終了までの配送です。</returns>
    internal async Task RunAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryNotice(out byte[]? frame) || controls.Reader.TryRead(out frame) || rows.Reader.TryRead(out frame))
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
