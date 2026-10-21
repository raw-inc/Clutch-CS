using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>待受の寿命と一件の設定世代を分けて管理します。</summary>
public sealed class NodeSourceContext
{
    private readonly Func<NodeExecutionContext> capture;
    private readonly Func<NodeEmission, CancellationToken, ValueTask> emit;
    private readonly Func<bool> stopRequested;
    private readonly Func<NodeSourceStatus, CancellationToken, ValueTask>? reportStatus;

    /// <summary>待受と一件受付の境界を作ります。</summary>
    public NodeSourceContext(
        NodeExecutionContext initial,
        Func<NodeExecutionContext> capture,
        Func<NodeEmission, CancellationToken, ValueTask> emit,
        Func<bool> stopRequested,
        Func<NodeSourceStatus, CancellationToken, ValueTask>? reportStatus = null,
        CancellationToken stopToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentNullException.ThrowIfNull(stopRequested);
        Initial = initial;
        this.capture = capture;
        this.emit = emit;
        this.stopRequested = stopRequested;
        this.reportStatus = reportStatus;
        StopToken = stopToken;
    }

    /// <summary>待受開始時の接続・場所・上流入力です。</summary>
    public NodeExecutionContext Initial
    {
        get;
    }

    /// <summary>時間指定の進行を担当変更後も維持する記録先。単独のノード実行では省略できます。</summary>
    public Scheduling.INodeScheduleCursor? Schedule
    {
        get; init;
    }

    /// <summary>再計時に対応したSourceだけへ渡す、受信時の単調増加時刻です。</summary>
    public System.Threading.Channels.ChannelReader<long>? Signals
    {
        get; init;
    }

    /// <summary>後続待受の開始許可と正常終了を所有する境界です。</summary>
    public ISourceScopeControl? Scopes
    {
        get; init;
    }

    /// <summary>予定正常終了が要求されたかを示します。</summary>
    public bool IsStopRequested => StopToken.IsCancellationRequested || stopRequested();

    /// <summary>予定正常終了のために待受を起こす通知です。緊急取消とは区別します。</summary>
    public CancellationToken StopToken
    {
        get;
    }

    /// <summary>一件結果を発生させず、待受の利用可能性を通知します。</summary>
    /// <returns>状態通知の完了です。</returns>
    public ValueTask ReportStatusAsync(NodeSourceStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (reportStatus is not null)
        {
            return reportStatus(status, cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>次の一件の取得開始時に現在の設定版を固定します。</summary>
    /// <returns>取得完了まで保持する一件の文脈です。</returns>
    public NodeExecutionContext BeginItem()
    {
        return capture();
    }

    /// <summary>取得開始時の文脈と結果を後続へ渡します。</summary>
    /// <returns>通知受付の完了です。</returns>
    public ValueTask EmitAsync(NodeExecutionContext item, NodeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);
        return emit(new NodeEmission(item, result), cancellationToken);
    }
}
