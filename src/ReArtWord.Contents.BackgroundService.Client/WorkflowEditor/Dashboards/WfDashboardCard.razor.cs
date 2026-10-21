using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>論理名と一つの表示・操作だけを描画し、ポートや後続値を表示しません。</summary>
// 抑制理由: Razorが生成する公開partialと一致させます。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開partialと一致させます。")]
public sealed partial class WfDashboardCard
{
    /// <summary>カードと元ノードの参照です。</summary>
    [Parameter, EditorRequired]
    public WfDashboardReference Reference
    {
        get; set;
    } = null!;

    /// <summary>元WFから確認した表示状態です。</summary>
    [Parameter]
    public WfDashboardNodeState? State
    {
        get; set;
    }

    /// <summary>参照先クラスタです。</summary>
    [Parameter]
    public string? Cluster
    {
        get; set;
    }

    /// <summary>接続と表示情報が確認できているかです。</summary>
    [Parameter]
    public bool Available
    {
        get; set;
    }

    /// <summary>このカードの操作を受付中かです。</summary>
    [Parameter]
    public bool Busy
    {
        get; set;
    }

    /// <summary>共有トグル状態です。未確認はnullです。</summary>
    [Parameter]
    public bool? Toggle
    {
        get; set;
    }

    /// <summary>元ノードを一度だけ押します。</summary>
    [Parameter]
    public EventCallback Pressed
    {
        get; set;
    }

    /// <summary>Valueの入力を元WFへ保存します。</summary>
    [Parameter]
    public EventCallback<WfDashboardValueInput> ValueChanged
    {
        get; set;
    }

    /// <summary>媒体本体を含まない、確認済みのプレビューです。</summary>
    // 抑制理由: 描画時に不変の取得済み一覧を受け取ります。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "描画時に不変の取得済み一覧を受け取ります。")]
    public IReadOnlyList<NodePreviewSnapshot> Snapshots
    {
        get; set;
    } = [];

    private string text = string.Empty;
    private string? revision, sentText;
    private bool edited;
    private static readonly JsonElement empty = JsonSerializer.SerializeToElement(new { });
    private NodeDefinition PreviewNode => new(Reference.NodeId, Reference.NodeType, empty);
    private bool IsToggle => Reference.NodeType is "control.toggleButton";
    private bool CanEdit => Available && !Busy && State is { ValueType: not null, Revision: not null, Problem: null };
    private bool CanPress => Available && !Busy && State is { Active: true, Problem: null } && (!IsToggle || Toggle is not null);

    /// <summary>未編集中だけ最新値を反映し、入力中の文字列と比較版は維持します。</summary>
    protected override void OnParametersSet()
    {
        if (edited && !Busy && string.Equals(State?.ValueText, text, StringComparison.Ordinal)
            && !string.Equals(State?.Revision, revision, StringComparison.Ordinal))
        {
            edited = false;
            sentText = null;
        }
        if (!edited)
        {
            text = State?.ValueText ?? string.Empty;
            revision = State?.Revision;
        }
    }

    /// <summary>型を切り替えず、入力開始時の版に対する変更を保持します。</summary>
    private void Input(ChangeEventArgs args)
    {
        if (!edited)
        {
            revision = State?.Revision;
        }
        text = args.Value?.ToString() ?? string.Empty;
        edited = !string.Equals(text, State?.ValueText, StringComparison.Ordinal);
        sentText = null;
    }

    /// <summary>フォーカス確定時だけ元WFへの保存を通知します。</summary>
    /// <returns>一回の明示入力の受付です。</returns>
    private async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanEdit && edited && revision is not null && !string.Equals(sentText, text, StringComparison.Ordinal))
        {
            sentText = text;
            await ValueChanged.InvokeAsync(new(revision, text)).ConfigureAwait(true);
        }
    }

    /// <summary>Enterで確定し、Escapeでは未保存の入力だけを戻します。</summary>
    /// <returns>キーボード入力の反映です。</returns>
    private Task KeyAsync(KeyboardEventArgs args, CancellationToken cancellationToken = default)
    {
        string enter = "Enter", escape = "Escape";
        if (string.Equals(args.Key, escape, StringComparison.Ordinal))
        {
            edited = false;
            text = State?.ValueText ?? string.Empty;
            revision = State?.Revision;
            sentText = null;
        }
        bool multiline = State?.ValueType is "array" or "object";
        if (!multiline && string.Equals(args.Key, enter, StringComparison.Ordinal) && !args.ShiftKey)
        {
            return CommitAsync(cancellationToken);
        }
        return Task.CompletedTask;
    }
}
