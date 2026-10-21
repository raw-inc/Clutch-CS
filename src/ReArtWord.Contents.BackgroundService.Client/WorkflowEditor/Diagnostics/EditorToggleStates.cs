using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>開いているWFの共有トグル状態だけを読み、表示のためにノードを実行しません。</summary>
internal sealed class EditorToggleStates(ManagementApi api)
{
    private string? currentCluster, currentWorkflow;
    private DateTimeOffset nextRead;
    private int generation;

    // 抑制理由: 公開後の一覧を変更せず、新しい参照への置換をキャンバスの再描画判定に使用します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "読み取り側が保持するスナップショットを変更せず、更新時に一覧を置換します。")]
    private Dictionary<string, bool?> values = new(StringComparer.Ordinal);

    /// <summary>別WFの状態を表示しないため、表示先の識別子と照合します。</summary>
    /// <returns>同じWFで確認できた状態だけです。nullは未確認です。</returns>
    internal IReadOnlyDictionary<string, bool?> Find(string? cluster, string workflow)
    {
        if (string.Equals(cluster, currentCluster, StringComparison.Ordinal) && string.Equals(workflow, currentWorkflow, StringComparison.Ordinal))
        {
            return values;
        }
        return System.Collections.Immutable.ImmutableDictionary<string, bool?>.Empty;
    }

    /// <summary>押下後は旧状態を確定表示せず、次の状態照会を早めます。</summary>
    internal void Invalidate(string node)
    {
        generation++;
        values = values.Where(item => !string.Equals(item.Key, node, StringComparison.Ordinal)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        nextRead = DateTimeOffset.MinValue;
    }

    /// <summary>画面の再表示と切断時は、以前の確認結果を現在値として残しません。</summary>
    internal void Reset()
    {
        if (currentCluster is null && currentWorkflow is null && values.Count == 0)
        {
            return;
        }
        generation++;
        currentCluster = null;
        currentWorkflow = null;
        values = new(StringComparer.Ordinal);
        nextRead = DateTimeOffset.MinValue;
    }

    /// <summary>既存の共有状態APIを使い、3秒間隔・最大4並列で現在値を確認します。</summary>
    /// <returns>確認できた状態への置換です。失敗時はOFFと断定しません。</returns>
    internal async Task RefreshAsync(string cluster, string peer, WorkflowDefinition document, CancellationToken cancellationToken = default)
    {
        bool changed = !string.Equals(cluster, currentCluster, StringComparison.Ordinal) || !string.Equals(document.Id, currentWorkflow, StringComparison.Ordinal);
        string[] nodes = document.Nodes.Where(node => node.Type is "control.toggleButton").Select(node => node.Id).ToArray();
        if (!changed && nodes.Length == values.Count && nodes.All(values.ContainsKey) && api.UtcNow < nextRead)
        {
            return;
        }
        currentCluster = cluster;
        currentWorkflow = document.Id;
        if (changed)
        {
            values = new(StringComparer.Ordinal);
        }
        int requestedGeneration = ++generation;
        nextRead = api.UtcNow.AddSeconds(3);
        Dictionary<string, bool?> found = new(StringComparer.Ordinal);
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            foreach (string[] batch in nodes.Chunk(4))
            {
                bool?[] states = await Task.WhenAll(batch.Select(node => ReadStateAsync(api, cluster, peer, document.Id, node, timeout.Token))).ConfigureAwait(true);
                for (int index = 0; index < batch.Length; index++)
                {
                    found[batch[index]] = states[index];
                }
                if (timeout.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        if (requestedGeneration != generation || !string.Equals(currentCluster, cluster, StringComparison.Ordinal) || !string.Equals(currentWorkflow, document.Id, StringComparison.Ordinal))
        {
            return;
        }
        if (changed || values.Count != found.Count || found.Any(item => !values.TryGetValue(item.Key, out bool? current) || current != item.Value))
        {
            values = found;
        }
    }

    /// <summary>保存値がない初回だけOFFとし、読取り失敗と区別します。</summary>
    /// <returns>保存確認済みのON/OFF、または未確認です。</returns>
    internal static async Task<bool?> ReadStateAsync(ManagementApi api, string cluster, string peer, string workflow, string node, CancellationToken cancellationToken = default)
    {
        try
        {
            InternalDataReadResult? result = await api.PostForPeerAsync<InternalDataReadResult>("/api/internal-data/read",
                new InternalDataTarget(workflow, EInternalDataScope.Shared, ToggleButtonState.Key(node)), peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (result?.ConfirmedKnown != true)
            {
                return null;
            }
            if (ToggleButtonState.TryRead(result.ConfirmedValue, out ToggleButtonState state))
            {
                return state.IsOn;
            }
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
