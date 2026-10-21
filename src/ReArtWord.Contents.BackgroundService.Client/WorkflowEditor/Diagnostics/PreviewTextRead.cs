using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>取得済みテキストを次の成功まで維持し、遅い応答と失敗した取得IDを採用しません。</summary>
internal sealed class PreviewTextRead
{
    private int generation;
    private Guid? pending;
    private CancellationTokenSource? reading;
    private Task completion = Task.CompletedTask;

    internal NodePreviewSnapshot? Snapshot
    {
        get; private set;
    }

    internal string? Text
    {
        get; private set;
    }

    internal string? Problem
    {
        get; private set;
    }

    internal bool Loading => pending is not null;

    /// <summary>選択先の変更で、本文と開始済み応答を同時に無効にします。</summary>
    internal void Clear()
    {
        generation++;
        reading?.Cancel();
        pending = null;
        Snapshot = null;
        Text = Problem = null;
    }

    /// <summary>読取りだけを最大3回試し、成功した本文と取得時刻を一緒に置き換えます。</summary>
    /// <returns>新しい本文の取得、または前回表示を維持した失敗です。</returns>
    // 抑制理由: 取消Taskと前の取得をReadOwnedAsyncで観測し、新しい取得Taskを呼出元へ返します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "completionを呼出元へ返し、前の取得も次の取得からawaitします。")]
    internal Task UpdateAsync(NodePreviewSnapshot snapshot, Func<CancellationToken, Task<byte[]>> read, CancellationToken cancellationToken = default)
    {
        if (Snapshot?.Id == snapshot.Id || pending == snapshot.Id)
        {
            return Task.CompletedTask;
        }
        int version = ++generation;
        Task cancel = reading?.CancelAsync() ?? Task.CompletedTask;
        Task previous = completion;
        completion = ReadOwnedAsync(snapshot, read, previous, cancel, version, cancellationToken);
        return completion;
    }

    /// <summary>前回の取消完了を待ってから最新の一件だけを読み、古い待機を新しい要求へ増幅しません。</summary>
    /// <returns>この取得が所有する取消資源も含む完了です。</returns>
    private async Task ReadOwnedAsync(NodePreviewSnapshot snapshot, Func<CancellationToken, Task<byte[]>> read, Task previous, Task cancel, int version, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        owned.CancelAfter(TimeSpan.FromSeconds(5));
        reading = owned;
        pending = snapshot.Id;
        Problem = null;
        try
        {
            await cancel.ConfigureAwait(true);
            await previous.ConfigureAwait(true);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(snapshot.Length, 65536);
            for (int attempt = 0; attempt < 3 && version == generation; attempt++)
            {
                try
                {
                    byte[] bytes = await read(owned.Token).ConfigureAwait(true);
                    if (version == generation && !owned.IsCancellationRequested)
                    {
                        Text = Encoding.UTF8.GetString(bytes);
                        Snapshot = snapshot;
                    }
                    return;
                }
                catch (Exception exception) when (attempt < 2 && exception is HttpRequestException or IOException or JsonException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150), owned.Token).ConfigureAwait(true);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException or ArgumentException)
        {
            if (version == generation && !cancellationToken.IsCancellationRequested)
            {
                Problem = "preview.unavailable";
            }
        }
        finally
        {
            if (ReferenceEquals(reading, owned))
            {
                reading = null;
            }
            if (version == generation)
            {
                pending = null;
            }
        }
    }
}
