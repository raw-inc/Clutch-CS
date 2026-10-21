using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Scripting;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;

/// <summary>クラスタ・文書・診断元を分け、古い検証を新しい草稿へ表示しません。</summary>
internal sealed class EditorDiagnostics
{
    private static readonly HashSet<string> codeTypes = new(["value.code", "script.csharp", "script.csharp-source", "script.python", "script.javascript"], StringComparer.Ordinal);
    private readonly Dictionary<(string? Cluster, string Workflow), DiagnosticState> states = [];
    private static readonly ConditionalWeakTable<WorkflowDefinition, DiagnosticFingerprint> fingerprints = new();

    /// <summary>入力値を含まない、処理中と成否の表示状態です。</summary>
    internal EditorValueStates Values
    {
        get;
    } = new();

    /// <summary>入力と現在のコードの解析を再計算し、編集済み文書の古い検証は解除します。</summary>
    internal void UpdateDraft(string? cluster, EditorDraft draft)
    {
        DiagnosticState state = State(cluster, draft.Document.Id);
        string fingerprint = Fingerprint(draft.Document);
        if (!string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            state.Validation.Clear();
            state.Fingerprint = fingerprint;
        }
        state.DraftRevision = draft.BaseRevision;
        state.Dirty = draft.Dirty;
        state.Local.Clear();
        foreach (string key in draft.InputDrafts?.Keys ?? [])
        {
            string nodeId = key.Split('/')[0];
            state.Local.Add(new("editor.input.invalid", "Check the value type and JSON format.", nodeId));
        }
        WorkflowSettingValues values = new(draft.Document);
        foreach (NodeDefinition node in draft.Document.Nodes)
        {
            if (codeTypes.Contains(node.Type))
            {
                NodeDefinition effective = values.Resolve(node);
                ScriptAnalysis? report = ScriptSettings.Latest(effective.Type, effective.Settings);
                foreach (string diagnostic in report?.Diagnostics ?? [])
                {
                    state.Local.Add(new("script.analysis", diagnostic, node.Id));
                }
            }
        }
        if (!string.Equals(state.RunRevision, draft.BaseRevision, StringComparison.Ordinal) || draft.Dirty)
        {
            state.Execution.Clear();
        }
    }

    /// <summary>応答の元になった文書が現在と同じ場合だけ検証を保持します。</summary>
    internal void SetValidation(string? cluster, WorkflowDefinition checkedDocument, WorkflowDefinition current, IReadOnlyList<ValidationIssue> issues)
    {
        string fingerprint = Fingerprint(checkedDocument);
        if (!ReferenceEquals(checkedDocument, current) && !string.Equals(fingerprint, Fingerprint(current), StringComparison.Ordinal))
        {
            return;
        }
        DiagnosticState state = State(cluster, checkedDocument.Id);
        state.Fingerprint = fingerprint;
        state.Validation.Clear();
        state.Validation.AddRange(issues);
    }

    /// <summary>実行結果の失敗・未実行・結果不明をノードへ対応させます。</summary>
    internal void SetRun(string? cluster, WorkflowRunStatus result)
    {
        if (result.Report is not { } report)
        {
            return;
        }
        DiagnosticState state = State(cluster, result.WorkflowId);
        state.RunRevision = result.Revision;
        state.Execution.Clear();
        state.Execution.AddRange(report.Nodes.Where(node => node.Outcome is EOutcome.Failed or EOutcome.Unknown || node.Targets.Any(target => IsProblem(target.Result.Outcome, target.Result.ReasonCode)))
            .Select(node => new ValidationIssue("editor.execution", "Execution: " + node.Outcome, node.NodeId)));
    }

    /// <summary>観測した待受の回復・終了を反映し、他クラスタの状態には触れません。</summary>
    internal void SetSources(string? cluster, IReadOnlyList<SourceView> sources)
    {
        foreach (DiagnosticState state in states.Where(pair => string.Equals(pair.Key.Cluster, cluster, StringComparison.Ordinal)).Select(pair => pair.Value))
        {
            state.Sources.Clear();
        }
        IEnumerable<SourceView> latest = sources.GroupBy(source => (source.WorkflowId, source.NodeId, source.PeerId))
            .Select(group => group.OrderByDescending(source => source.StartedAt).First());
        foreach (IGrouping<string, SourceView> group in latest.GroupBy(source => source.WorkflowId, StringComparer.Ordinal))
        {
            State(cluster, group.Key).Sources.AddRange(group.Where(source => source.Status.State is ESourceState.Failed or ESourceState.Unavailable)
                .Select(source => new ValidationIssue(source.Status.ReasonCode ?? "source.unavailable", source.Status.Message ?? source.Status.ReasonCode ?? "Source unavailable", source.NodeId)));
        }
    }

    /// <summary>同じノード・対象の最新結果を使い、自動実行の回復でもエラーを解除します。</summary>
    internal void SetHistory(string? cluster, IReadOnlyList<NodeExecutionObservation> history)
    {
        foreach (IGrouping<string, NodeExecutionObservation> group in history.GroupBy(item => item.WorkflowId, StringComparer.Ordinal))
        {
            NodeExecutionObservation[] ordered = group.OrderByDescending(item => item.CompletedAt).ToArray();
            DiagnosticState state = State(cluster, group.Key);
            if (ordered[0].CompletedAt < state.HistoryTime)
            {
                continue;
            }
            state.HistoryTime = ordered[0].CompletedAt;
            state.HistoryRevision = ordered[0].Revision;
            state.History.Clear();
            foreach (NodeExecutionObservation target in ordered.Where(item => string.Equals(item.Revision, state.HistoryRevision, StringComparison.Ordinal)).DistinctBy(item => (item.NodeId, item.PeerId)))
            {
                if (IsProblem(target.Outcome, target.ReasonCode))
                {
                    state.History.Add(new(target.ReasonCode ?? "editor.execution", "Execution: " + target.Outcome + " · " + target.ReasonCode, target.NodeId));
                }
            }
        }
    }

    /// <summary>設定へ反映済みの値、対象外の入口・分岐・反復、指定どおりの間引き、明示取消をノード不具合として強調しません。</summary>
    /// <returns>確認済み失敗、結果不明、または意図した未実行以外の拒否です。</returns>
    private static bool IsProblem(EOutcome outcome, string? reason)
        => outcome is EOutcome.Failed or EOutcome.Unknown || (outcome == EOutcome.NotExecuted && reason is not null && !ExecutionResultReasons.IsExpectedSkip(reason));

    /// <summary>同一クラスタの同一ワークフローにだけ問題を表示します。</summary>
    /// <returns>診断元を統合した現在の問題です。</returns>
    internal IReadOnlyList<ValidationIssue> Issues(string? cluster, string workflow)
    {
        if (!states.TryGetValue((cluster, workflow), out DiagnosticState? state))
        {
            return [];
        }
        List<ValidationIssue> issues = [.. state.Local, .. state.Validation, .. state.Execution, .. state.Sources];
        if (!state.Dirty && (state.DraftRevision is null || string.Equals(state.DraftRevision, state.HistoryRevision, StringComparison.Ordinal)))
        {
            issues.AddRange(state.History);
        }
        return issues;
    }

    /// <summary>タブの配下に確認済みの問題があるかを返します。</summary>
    /// <returns>未観測のクラスタはエラーとは扱いません。</returns>
    internal bool ClusterHasErrors(string? cluster) => states.Keys.Any(key => string.Equals(key.Cluster, cluster, StringComparison.Ordinal) && Issues(key.Cluster, key.Workflow).Count != 0);

    /// <summary>位置・寸法・論理名だけの編集で診断を無効にしません。</summary>
    /// <returns>実行内容の指紋です。</returns>
    private static string Fingerprint(WorkflowDefinition document) => fingerprints.GetValue(document, static value => new(ContractJson.Hash(value with
    {
        Nodes = value.Nodes.Select(node => node with { Position = null, Size = null, LogicalName = null }).ToArray(),
    }))).Value;

    /// <summary>表示中の所有定義と一緒に回収する、実行設定だけの比較結果です。</summary>
    private sealed record DiagnosticFingerprint(string Value);

    /// <summary>画面の観測キャッシュを有限件に保ちます。</summary>
    /// <returns>一つの文書の診断保持先です。</returns>
    private DiagnosticState State(string? cluster, string workflow)
    {
        if (!states.TryGetValue((cluster, workflow), out DiagnosticState? state))
        {
            if (128 <= states.Count)
            {
                states.Remove(states.Keys.First());
            }
            state = new();
            states.Add((cluster, workflow), state);
        }
        return state;
    }

    /// <summary>それぞれの確認結果を独立して置き換えます。</summary>
    private sealed record DiagnosticState
    {
        internal DateTimeOffset HistoryTime
        {
            get; set;
        }

        internal string? HistoryRevision
        {
            get; set;
        }

        internal string? DraftRevision
        {
            get; set;
        }

        internal bool Dirty
        {
            get; set;
        }

        internal List<ValidationIssue> History
        {
            get;
        } = [];

        internal string? Fingerprint
        {
            get; set;
        }

        internal string? RunRevision
        {
            get; set;
        }

        internal List<ValidationIssue> Local
        {
            get;
        } = [];

        internal List<ValidationIssue> Validation
        {
            get;
        } = [];

        internal List<ValidationIssue> Execution
        {
            get;
        } = [];

        internal List<ValidationIssue> Sources
        {
            get;
        } = [];

    }
}
