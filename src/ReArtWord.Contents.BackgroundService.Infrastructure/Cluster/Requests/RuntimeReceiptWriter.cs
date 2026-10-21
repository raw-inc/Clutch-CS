using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>実行結果の記録を実行経路から分離し、終了時には所有workerの完了を待ちます。</summary>
internal sealed class RuntimeReceiptWriter
{
    private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(512);
    private readonly IDurableRepository repository;
    private readonly string scope;
    private readonly Task worker;
    private string? problem;

    /// <summary>起動実体の記録先と有限キューを固定します。</summary>
    // 抑制理由: workerをフィールドで所有し、PeerのDrainAsyncでキュー完了と全件の保存終了を必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有workerはDrainAsyncで完了を待ち、未所有の背景処理を作りません。")]
    internal RuntimeReceiptWriter(IDurableRepository repository, string scope)
    {
        this.repository = repository;
        this.scope = scope;
        worker = Task.Run(() => RunAsync());
    }

    /// <summary>直近の保存失敗を値や秘密情報を含めず公開します。</summary>
    internal string? Problem => Volatile.Read(ref problem);

    /// <summary>呼出側で予約した枠だけを使用し、送信完了をディスク待ちにしません。</summary>
    /// <returns>予約された有限枠へ投入できた場合だけtrueです。</returns>
    internal bool TryWrite(Guid id, RequestReceipt receipt, Action<bool> completed) => queue.Writer.TryWrite(new(id, receipt, completed));

    /// <summary>新規受付停止後に残った結果を保存します。</summary>
    /// <returns>所有workerの終了です。</returns>
    internal Task DrainAsync(CancellationToken cancellationToken = default)
    {
        queue.Writer.TryComplete();
        return worker.WaitAsync(cancellationToken);
    }

    /// <summary>同じ時点に届いた完了を既存の耐久CASへ並行投入し、保存層の一括取引を利用します。</summary>
    /// <returns>キューの全件処理です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            List<Entry> batch = [];
            while (batch.Count < 128 && queue.Reader.TryRead(out Entry? item))
            {
                batch.Add(item);
            }
            await Task.WhenAll(batch.Select(item => SaveAsync(item, cancellationToken))).ConfigureAwait(false);
        }
    }

    /// <summary>保存の再試行をノードの再実行へ置き換えません。</summary>
    /// <returns>一件の保存結果の通知です。</returns>
    private async Task SaveAsync(Entry entry, CancellationToken cancellationToken = default)
    {
        bool saved = false;
        try
        {
            saved = await RequestReceiptPersistence.WriteAsync(repository, scope, entry.Id.ToString("N"), null, NodeValue.FromObject(entry.Receipt), cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            saved = false;
        }
        if (!saved)
        {
            Volatile.Write(ref problem, "Execution.BufferedReceiptUnconfirmed");
        }
        entry.Completed(saved);
    }

    /// <summary>実行中の入力やdelegateを保持しない、完了記録だけの一項目です。</summary>
    private sealed record Entry(Guid Id, RequestReceipt Receipt, Action<bool> Completed);
}
