using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Safety;

/// <summary>人為停止を維持し、一時続行を元の障害と生存実体に束縛します。</summary>
public sealed class ManualSafetyService : IDisposable
{
    private readonly PeerCluster cluster;
    private readonly IDurableRepository repository;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private readonly ManualSafetyRouter router;
    private readonly ManualSafetyContinuations continuations;
    private readonly ManualSafetyAdmission admission;
    private bool loaded;

    // 抑制理由: 一件の入力へ途中まで更新した配列を見せず参照ごと交換します。
    [SuppressMessage("SourceQuality", "RAW170", Justification = "状態一覧をVolatileで一度に公開するためです。")]
    private ManualSafetyEntry[] current = [];

    /// <summary>共通操作記録とCoordinator受付を同じ既存配送境界へ登録します。</summary>
    public ManualSafetyService(PeerCluster cluster, IDurableRepository repository)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        this.cluster = cluster;
        this.repository = repository;
        router = new ManualSafetyRouter(cluster, repository);
        continuations = new ManualSafetyContinuations(cluster, repository);
        admission = new ManualSafetyAdmission(cluster, repository, (workflow, scope) => AutomaticStopCheck?.Invoke(workflow, scope));
        cluster.RegisterHandler("safety.change", admission.AcceptAsync);
    }

    /// <summary>現有効ノードの通常判断です。対象や版が不明ならnullを返します。</summary>
    public Func<string, string, bool?>? AutomaticStopCheck
    {
        get; set;
    }

    /// <summary>保存状態の取込を完了したかを示します。</summary>
    public bool Loaded => Volatile.Read(ref loaded);

    /// <summary>現在実効の状態を示し、保存trueを無条件に続行中と表示しません。</summary>
    public IReadOnlyList<ManualSafetyStatus> Current => Volatile.Read(ref current).GroupBy(item => (item.Setting.WorkflowId, item.Setting.Scope)).Select(group => Status(group.ToArray())).ToArray();

    /// <summary>元配送先の操作記録だけを読み、結果不明の要求を再実行しません。</summary>
    /// <returns>元受付結果、ローカルに元要求がなければnullです。</returns>
    public Task<DurableReceipt?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) => router.QueryAsync(operationId, cancellationToken);

    /// <summary>新規IDだけを現受付先へ固定し、既存要求は元操作記録から確認します。</summary>
    /// <returns>受付側が確認した元結果です。</returns>
    public async Task<DurableReceipt> ChangeAsync(ManualSafetyChange request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope);
        if (request.OperationId == Guid.Empty || (request.Stopped && request.Continued))
        {
            return ManualSafetyRouter.Result(request, EOutcome.NotExecuted, "safety.invalidManualMode");
        }
        DurableReceipt result;
        try
        {
            result = await router.SendAsync(ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Copy(request), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = ManualSafetyRouter.Result(request, EOutcome.Unknown, "safety.waitCancelled");
        }
        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>共有状態と元操作別の失効を読み、原子的にローカル判断へ公開します。</summary>
    /// <returns>取込と観測済み失効の保存完了です。</returns>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool confirmed = false;
        try
        {
            bool detached = IsDetached();
            IReadOnlyList<DurableEntry> entries = await repository.ListAsync(ManualSafetyAdmission.Scope(cluster.LocalPeerId, detached), cancellationToken).ConfigureAwait(false);
            ManualSafetyEntry[] items = ManualSafetyState.Frontier(ManualSafetyState.Read(entries, detached));
            foreach (ManualSafetyEntry item in items)
            {
                await continuations.ReadAsync(item.Setting.OperationId, detached, cancellationToken).ConfigureAwait(false);
                _ = continuations.Evaluate(item.Setting, AutomaticStopCheck?.Invoke(item.Setting.WorkflowId, item.Setting.Scope));
            }
            Volatile.Write(ref current, items);
            confirmed = true;
            await continuations.PublishAsync(detached, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref loaded, confirmed);
            refresh.Release();
        }
    }

    /// <summary>ノードとGroupの人為停止を一時続行より優先します。</summary>
    /// <returns>新規入力に人為停止を適用するか。</returns>
    public bool IsStopped(string workflowId, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Volatile.Read(ref current).Any(item => item.Setting.Stopped && Matches(item.Setting, workflowId, node));
    }

    /// <summary>生存実体と現在条件を再確認した続行だけを適用します。</summary>
    /// <returns>現在の一時続行が成立するか。</returns>
    public bool IsContinued(string workflowId, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (IsStopped(workflowId, node))
        {
            return false;
        }
        return Volatile.Read(ref current).Any(item => Matches(item.Setting, workflowId, node) && continuations.Evaluate(item.Setting, AutomaticStopCheck?.Invoke(item.Setting.WorkflowId, item.Setting.Scope)).Active);
    }

    /// <summary>保存要求と現在実効を分離した状態を作ります。</summary>
    /// <returns>画面と実行判定に共通の状態です。</returns>
    private ManualSafetyStatus Status(ManualSafetyEntry[] items)
    {
        ManualSafetyEntry first = items[0];
        ManualSafetySetting setting = first.Setting;
        (bool Active, string? Reason)[] decisions = items.Select(item => continuations.Evaluate(item.Setting, AutomaticStopCheck?.Invoke(item.Setting.WorkflowId, item.Setting.Scope))).ToArray();
        bool stopped = items.Any(item => item.Setting.Stopped);
        Guid[] operations = items.Select(item => item.Setting.OperationId).Order().ToArray();
        if (items.Length == 1)
        {
            return new(setting.WorkflowId, setting.Scope, stopped, first.Version, first.LocalOnly, !stopped && decisions[0].Active, setting.OperationId, setting.Acceptance, setting.Continued, decisions[0].Reason, operations);
        }
        return new(setting.WorkflowId, setting.Scope, stopped, ReArtWord.Contents.BackgroundService.Contracts.Serialization.ContractJson.Hash(operations), first.LocalOnly, !stopped && decisions.Any(item => item.Active), null, null,
            items.Any(item => item.Setting.Continued), "safety.unorderedOperationsObserved", operations);
    }

    /// <summary>Scope名をノードまたは安全Groupへ対応付けます。</summary>
    /// <returns>当該ノードに属する操作か。</returns>
    private static bool Matches(ManualSafetySetting setting, string workflowId, NodeDefinition node) => string.Equals(setting.WorkflowId, workflowId, StringComparison.Ordinal)
        && (string.Equals(setting.Scope, node.Id, StringComparison.Ordinal) || string.Equals(setting.Scope, node.Safety?.Group, StringComparison.Ordinal));

    /// <summary>ローカル離脱の保存先を区別します。</summary>
    /// <returns>この実体が離脱中か。</returns>
    private bool IsDetached() => cluster.IsDetached;

    /// <summary>所有する受付と取込の排他を解放します。</summary>
    public void Dispose()
    {
        admission.Dispose();
        router.Dispose();
        refresh.Dispose();
    }

}
