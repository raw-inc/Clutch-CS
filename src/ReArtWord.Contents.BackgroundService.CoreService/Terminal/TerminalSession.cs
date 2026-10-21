using System.Diagnostics.CodeAnalysis;
using Porta.Pty;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Terminal;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;

namespace ReArtWord.Contents.BackgroundService.CoreService.Terminal;

/// <summary>一つのPTYと有限の出力を所有し、所有者変更と入力を直列化します。</summary>
internal sealed class TerminalSession : IAsyncDisposable
{
    private readonly IPtyConnection process;
    private readonly SemaphoreSlim input = new(1, 1);
    private readonly Lock sync = new();
    private readonly byte[] output = new byte[256 * 1024];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reading;
    private TerminalView view;
    private long end;
    private TaskCompletionSource? changed;
    private readonly SemaphoreSlim attaching = new(1, 1);
    private TerminalConnection? connection;
    private int columns = 100, rows = 30;
    private readonly SessionFailureBoundary failures;

    /// <summary>プロセスの所有権を受け取り、出力を画面と独立して読み続けます。</summary>
    // 抑制理由: 出力Taskをフィールドで所有し、終了時にawaitします。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "出力タスクはreadingで保持しDisposeAsyncで終了を待ちます。")]
    internal TerminalSession(IPtyConnection process, Guid id, string shell, Guid owner, SessionFailureBoundary failures, bool elevated = false, bool system = false)
    {
        this.process = process;
        this.failures = failures;
        view = new(id, shell, owner, 0, DateTimeOffset.UtcNow, false, elevated, system);
        reading = failures.RunAsync(() => ReadProcessAsync(lifetime.Token), "Terminal", id, "output", lifetime.Token);
    }

    /// <summary>所有権を含む不変の状態を返します。</summary>
    internal TerminalView View
    {
        get
        {
            using (sync.EnterScope())
            {
                return view;
            }
        }
    }

    /// <summary>有限の出力を絶対位置で返し、取りこぼしを検出できます。</summary>
    /// <returns>最大16KiBの出力です。</returns>
    internal TerminalOutput Read(long offset, int maximum = 16384, long ceiling = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using (sync.EnterScope())
        {
            long start = Math.Clamp(offset, Math.Max(0, end - output.Length), end);
            byte[] bytes = new byte[(int)Math.Min(maximum, Math.Max(0, Math.Min(end, ceiling) - start))];
            int position = (int)(start % output.Length), first = Math.Min(bytes.Length, output.Length - position);
            output.AsSpan(position, first).CopyTo(bytes);
            output.AsSpan(0, bytes.Length - first).CopyTo(bytes.AsSpan(first));
            return new(start, start + bytes.Length, Convert.ToBase64String(bytes), view);
        }
    }

    /// <summary>再接続時の履歴と接続後の出力を区切る絶対位置です。</summary>
    internal long OutputEnd
    {
        get
        {
            using (sync.EnterScope())
            {
                return end;
            }
        }
    }

    /// <summary>出力・所有者・終了状態が変わるまで待ち、空読みを繰り返しません。</summary>
    /// <returns>次の変化または取消しです。</returns>
    internal Task WaitAsync(long offset, Guid owner, CancellationToken cancellationToken = default)
    {
        using (sync.EnterScope())
        {
            if (offset != end || view.Owner != owner || view.Exited)
            {
                return Task.CompletedTask;
            }

            changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return changed.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>sync内で状態を更新した後、待機中の接続を起こします。</summary>
    private void Pulse()
    {
        TaskCompletionSource? previous = changed;
        changed = null;
        previous?.TrySetResult();
    }

    /// <summary>古い表示接続だけを閉じ、PTYを維持して新しい接続許可を返します。</summary>
    /// <returns>一回限りのTCP受付情報です。</returns>
    // 抑制理由: 接続の所有権をフィールドへ保持し、置換時とDisposeAsyncで解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "接続はセッションが所有して解放します。"), SuppressMessage("SourceQuality", "RAW140", Justification = "保持接続を新しい接続の生成前に停止します。")]
    internal async Task<TerminalTicket> AttachAsync(string address, TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        await attaching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Owner == Guid.Empty || request.ConnectionId == Guid.Empty || View.Owner != request.Owner)
            {
                throw new InvalidOperationException("Terminal ownership changed.");
            }

            if (connection?.Id == request.ConnectionId)
            {
                return connection.Ticket;
            }

            TerminalConnection? previous = connection;
            connection = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            connection = new(address, this, request, failures);
            return connection.Ticket;
        }

        finally
        {
            attaching.Release();
        }
    }

    /// <summary>引継ぎを入力と同じ排他で処理し、旧所有者の入力を拒否します。</summary>
    /// <returns>新しい所有権です。</returns>
    internal async Task<TerminalView> ClaimAsync(Guid owner, CancellationToken cancellationToken = default)
    {
        if (owner == Guid.Empty)
        {
            throw new ArgumentException("Owner is required.", nameof(owner));
        }

        await input.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (sync.EnterScope())
            {
                view = view with
                {
                    Owner = owner,
                    Sequence = 0
                }
; Pulse(); return view;
            }
        }

        finally
        {
            input.Release();
        }
    }

    /// <summary>連番を消費した入力を再送で二重実行しません。</summary>
    /// <returns>次の受付連番です。</returns>
    internal async Task<TerminalView> InputAsync(TerminalRequest request, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Convert.FromBase64String(request.Data ?? string.Empty);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, 8192);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Columns, 20);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Columns, 400);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Rows, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Rows, 200);
        await input.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (sync.EnterScope())
            {
                if (view.Exited || request.Owner == Guid.Empty || view.Owner != request.Owner || view.Sequence < request.Sequence)
                {
                    throw new InvalidOperationException("Terminal ownership or input sequence changed.");
                }

                if (request.Sequence < view.Sequence)
                {
                    return view;
                }

                view = view with
                {
                    Sequence = view.Sequence + 1
                }
;
            }

            if (columns != request.Columns || rows != request.Rows)
            {
                process.Resize(request.Columns, request.Rows);
                columns = request.Columns; rows = request.Rows;
            }

            if (bytes.Length == 0)
            {
                return View;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WriterStream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await process.WriterStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            return View;
        }

        finally
        {
            input.Release();
        }
    }

    /// <summary>過去出力の先頭を上書きし、PTYの逆圧を画面寿命へ依存させません。</summary>
    /// <returns>出力終了です。</returns>
    private async Task ReadProcessAsync(CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[8192];
        try
        {
            int count;
            while (0 < (count = await process.ReaderStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)))
            {
                using (sync.EnterScope())
                {
                    int position = (int)(end % output.Length), first = Math.Min(count, output.Length - position);
                    buffer.AsSpan(0, first).CopyTo(output.AsSpan(position));
                    buffer.AsSpan(first, count - first).CopyTo(output);
                    end += count;

                    Pulse();
                }
            }
        }

        finally
        {
            using (sync.EnterScope())
            {
                view = view with
                {
                    Exited = true
                }
;
                Pulse();
            }
        }
    }

    /// <summary>プロセスと子プロセスを終了し、出力タスクと同期資源を回収します。</summary>
    /// <returns>終了確認です。</returns>
    // 抑制理由: IAsyncDisposable固定署名です。所有するI/Oを取消して完了を待ちます。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await attaching.WaitAsync().ConfigureAwait(false);
        try
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        finally
        {
            attaching.Release(); attaching.Dispose();
        }

        await input.WaitAsync().ConfigureAwait(false);
        try
        {
            await failures.RunAsync(lifetime.CancelAsync, "Terminal", View.SessionId, "cancel").ConfigureAwait(false);
            await failures.RunAsync(() =>
            {
                process.Kill();
                return Task.CompletedTask;
            }, "Terminal", View.SessionId, "kill").ConfigureAwait(false);
            await failures.RunAsync(() =>
            {
                process.Dispose();
                return Task.CompletedTask;
            }, "Terminal", View.SessionId, "release").ConfigureAwait(false);
            await reading.ConfigureAwait(false);
        }

        finally
        {
            input.Release();
            lifetime.Dispose();
            input.Dispose();
        }
    }
}
