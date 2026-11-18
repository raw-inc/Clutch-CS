using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Requests;

/// <summary>一回の要求の受付通知と完了タスクを共有し、同じIDの再送から処理を重複起動しません。</summary>
internal sealed record RequestOperation
{
    /// <summary>要求の登録中に実処理が先行しないよう、開始を遅延します。</summary>
    internal RequestOperation(string fingerprint, Func<RequestOperation, Task<ClusterResponse>> run)
    {
        Fingerprint = fingerprint;
        Completion = new(() => run(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>受付前も異内容の同じIDを拒否します。</summary>
    internal string Fingerprint
    {
        get;
    }

    /// <summary>永続受付が成立するまで副作用を開始済みと報告しません。</summary>
    internal TaskCompletionSource<ClusterResponse> Admission
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>依頼元の取消とは独立した所有タスクです。</summary>
    internal Lazy<Task<ClusterResponse>> Completion
    {
        get;
    }
}
