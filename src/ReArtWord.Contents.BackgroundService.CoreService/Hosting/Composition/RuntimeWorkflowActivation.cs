using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;

/// <summary>WFの実反映と復元対象の登録を、同じ構成変更の排他で扱います。</summary>
internal sealed class RuntimeWorkflowActivation(ServiceRuntime runtime) : IDisposable
{
    private readonly HashSet<string> snapshotScopes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>実設定の反映と復元可能な状態範囲の登録を行います。</summary>
    /// <returns>このPeerでの適用結果です。</returns>
    internal async Task<ConfigurationPhaseResult> ApplyAsync(CompiledWorkflow workflow, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await runtime.Sources.ApplyAsync(workflow, cancellationToken).ConfigureAwait(false);
            string shared = NodeStateScope.Shared(workflow.Definition.Id);
            string local = NodeStateScope.Peer(runtime.Cluster.LocalPeerId, workflow.Definition.Id);
            foreach (string scope in new[] { shared, local })
            {
                if (snapshotScopes.Add(scope))
                {
                    runtime.Snapshots.RegisterRuntime(new NodeStateSnapshotParticipant(scope, scope, runtime.Repository, runtime.Cluster, () => runtime.Sources.GetEffective(workflow.Definition.Id)?.Definition,
                        definition => runtime.Compiler.Compile(definition).Workflow?.OrderedNodes));
                }
            }
            return new ConfigurationPhaseResult(EOutcome.Succeeded);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>有効設定がない開始前状態へ戻します。</summary>
    /// <returns>受付の停止通知と有効設定除去の結果です。</returns>
    internal async Task<ConfigurationPhaseResult> RemoveAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        await runtime.Sources.RemoveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return new ConfigurationPhaseResult(EOutcome.Succeeded);
    }

    /// <summary>通常の受付終了後に構成変更の排他を解放します。</summary>
    public void Dispose() => gate.Dispose();
}
