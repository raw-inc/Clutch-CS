using System.Text;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>更新中のちらつき、読取り失敗、別の表示へ届く遅延応答を検証します。</summary>
public sealed class PreviewTextReadTests
{
    /// <summary>新しい値が取得できるまで、旧本文と旧時刻を組として表示します。</summary>
    /// <returns>二重取得をせず、成功時にだけ表示を差し替える確認です。</returns>
    [Fact]
    public async Task PendingReadPreservesVisibleValueAndCoalescesAsync()
    {
        PreviewTextRead state = new();
        NodePreviewSnapshot first = Snapshot(), next = Snapshot();
        await state.UpdateAsync(first, _ => Task.FromResult(Encoding.UTF8.GetBytes("first"))).ConfigureAwait(true);
        TaskCompletionSource<byte[]> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = state.UpdateAsync(next, _ => reply.Task);
        Assert.True(state.Loading);
        Assert.Equal("first", state.Text);
        Assert.Equal(first, state.Snapshot);
        await state.UpdateAsync(next, _ => throw new InvalidOperationException("Duplicate read")).ConfigureAwait(true);
        reply.SetResult(Encoding.UTF8.GetBytes("second"));
        await pending.ConfigureAwait(true);
        Assert.False(state.Loading);
        Assert.Equal("second", state.Text);
        Assert.Equal(next, state.Snapshot);
    }

    /// <summary>取得失敗を成功したIDとして記憶せず、同じ取得IDを再試行できます。</summary>
    /// <returns>前回表示・失敗表示・正常回復の確認です。</returns>
    [Fact]
    public async Task FailedCaptureCanBeReadAgainAsync()
    {
        PreviewTextRead state = new();
        NodePreviewSnapshot first = Snapshot(), next = Snapshot();
        await state.UpdateAsync(first, _ => Task.FromResult(Encoding.UTF8.GetBytes("first"))).ConfigureAwait(true);
        int requests = 0;
        await state.UpdateAsync(next, _ =>
        {
            requests++;
            throw new HttpRequestException("Unavailable");
        }).ConfigureAwait(true);
        Assert.Equal(3, requests);
        Assert.Equal("first", state.Text);
        Assert.Equal(first, state.Snapshot);
        Assert.Equal("preview.unavailable", state.Problem);
        await state.UpdateAsync(next, _ => Task.FromResult(Encoding.UTF8.GetBytes("recovered"))).ConfigureAwait(true);
        Assert.Equal("recovered", state.Text);
        Assert.Null(state.Problem);
        Assert.Equal(next, state.Snapshot);
    }

    /// <summary>次の値や別WFを表示した後に届く応答で画面を戻しません。</summary>
    /// <returns>取得順の逆転と明示解除の確認です。</returns>
    [Theory(Skip = "旧仕様: 古い本文取得を残したまま次の取得を並走させる。現行は旧取得を取り消して回収後に次へ進む。")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateResponseCannotReplaceNewerSelectionAsync(bool clear)
    {
        PreviewTextRead state = new();
        TaskCompletionSource<byte[]> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = state.UpdateAsync(Snapshot(), _ => reply.Task);
        if (clear)
        {
            state.Clear();
        }
        else
        {
            await state.UpdateAsync(Snapshot(), _ => Task.FromResult(Encoding.UTF8.GetBytes("newer"))).ConfigureAwait(true);
        }
        reply.SetResult(Encoding.UTF8.GetBytes("old"));
        await pending.ConfigureAwait(true);
        string? expected = clear ? null
                                 : "newer";
        Assert.Equal(expected, state.Text);
        Assert.False(state.Loading);
    }

    /// <summary>取消に協力しない旧読取りの回収後も、旧応答を新しい選択へ反映しません。</summary>
    /// <returns>取得順の逆転と明示解除の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainedLateResponseCannotReplaceNewerSelectionAsync(bool clear)
    {
        PreviewTextRead state = new();
        TaskCompletionSource<byte[]> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = state.UpdateAsync(Snapshot(), _ => reply.Task);
        Task newer = Task.CompletedTask;
        if (clear)
        {
            state.Clear();
        }
        else
        {
            newer = state.UpdateAsync(Snapshot(), _ => Task.FromResult(Encoding.UTF8.GetBytes("newer")));
        }
        reply.SetResult(Encoding.UTF8.GetBytes("old"));
        await pending.ConfigureAwait(true);
        await newer.ConfigureAwait(true);
        string? expected = clear ? null
                                 : "newer";
        Assert.Equal(expected, state.Text);
        Assert.False(state.Loading);
    }

    /// <summary>同じノードに到着する独立した取得結果です。</summary>
    /// <returns>テキストプレビューのメタデータです。</returns>
    private static NodePreviewSnapshot Snapshot() => new(Guid.NewGuid(), "flow", "revision", "preview", "peer", DateTimeOffset.UtcNow, "text/plain", 16);

    /// <summary>新しい取得は旧通信の取消完了を待ち、一つの本文取得だけを所有します。</summary>
    /// <returns>古い値の取消と、最新値だけの採用です。</returns>
    [Fact]
    public async Task NewSnapshotCancelsAndDrainsPreviousReadAsync()
    {
        PreviewTextRead state = new();
        int active = 0;
        CancellationToken oldToken = default;
        Task first = state.UpdateAsync(Snapshot(), async token =>
        {
            oldToken = token;
            Interlocked.Increment(ref active);
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        NodePreviewSnapshot latest = Snapshot();
        await state.UpdateAsync(latest, _ =>
        {
            Assert.Equal(0, Volatile.Read(ref active));
            return Task.FromResult(Encoding.UTF8.GetBytes("latest"));
        }).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        await first.ConfigureAwait(true);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(latest, state.Snapshot);
        Assert.Equal("latest", state.Text);
        Assert.Null(state.Problem);
    }

    /// <summary>表示対象を離れたときは、取得の完了を待たずに通信へ取消を伝えます。</summary>
    /// <returns>表示状態と取得資源の解放です。</returns>
    [Fact]
    public async Task ClearingSelectionCancelsItsReadAsync()
    {
        PreviewTextRead state = new();
        CancellationToken received = default;
        Task reading = state.UpdateAsync(Snapshot(), async token =>
        {
            received = token;
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return [];
        });
        state.Clear();
        await reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        Assert.True(received.IsCancellationRequested);
        Assert.Null(state.Snapshot);
        Assert.False(state.Loading);
        Assert.Null(state.Problem);
    }
}
