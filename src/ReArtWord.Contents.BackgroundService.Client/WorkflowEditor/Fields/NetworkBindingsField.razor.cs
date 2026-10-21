using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>実行PCごとに取得したNICとIPv4を、一回の設定編集として保存します。</summary>
// 抑制理由: Razorが生成する公開宣言と、同じコンポーネント型を構成します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせるため。")]
public sealed partial class NetworkBindingsField
{
    [Inject]
    private SettingChoiceProvider Choices
    {
        get; set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get; set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>設定を編集する通信ノードです。</summary>
    [Parameter]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>PC別のNICとIPv4の保存値です。</summary>
    [Parameter]
    public JsonElement Value
    {
        get; set;
    }

    /// <summary>接続値や編集保護で変更できないことを表します。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>ノード内では固定高の設定行に収め、説明を親のツールチップへまとめます。</summary>
    [Parameter]
    public bool Inline
    {
        get; set;
    }

    /// <summary>全行の変更を一回のUndo可能な操作として通知します。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    private readonly List<BindingRow> rows = [];
    private bool open, loading, invalidSaved;
    private int generation;
    private string? cluster, problem;
    private string original = string.Empty, additionalHost = string.Empty;
    private int SavedCount => Value.ValueKind == JsonValueKind.Array ? Value.GetArrayLength()
                                                                     : 0;
    private string CurrentValue => Value.ValueKind == JsonValueKind.Undefined ? string.Empty
                                                                              : Value.GetRawText();
    private bool CanEdit => !Disabled && generation == Model.Generation && string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal)
        && string.Equals(original, CurrentValue, StringComparison.Ordinal) && !Model.Locks.Node(Model.Draft.Document.Id, NodeId);

    /// <summary>保存値を保持し、現在の実行PC候補を未割り当ての行として追加します。</summary>
    /// <returns>PC別のネットワーク候補の取得完了です。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cluster = Api.SelectedClusterId;
        generation = Model.Generation;
        original = CurrentValue;
        problem = null;
        invalidSaved = false;
        rows.Clear();
        open = true;
        try
        {
            HostNetworkBinding[] saved = Value.ValueKind == JsonValueKind.Undefined ? []
                                                                                    : Value.Deserialize<HostNetworkBinding[]>(ContractJson.Options) ?? [];
            foreach (HostNetworkBinding binding in saved)
            {
                if (binding is null || string.IsNullOrWhiteSpace(binding.HostId) || rows.Any(row => string.Equals(row.HostId, binding.HostId, StringComparison.Ordinal)))
                {
                    throw new JsonException();
                }
                rows.Add(new(binding.HostId) { Assigned = true, InterfaceId = binding.InterfaceId ?? string.Empty, LocalAddress = binding.LocalAddress ?? string.Empty });
            }
        }
        catch (JsonException)
        {
            invalidSaved = true;
            problem = "Saved network assignments are invalid. The original settings have been retained.";
            return;
        }
        NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
        IReadOnlyList<string> hosts = [];
        if (node is not null)
        {
            hosts = Choices.NetworkHosts(node);
        }
        foreach (string host in hosts)
        {
            if (!rows.Any(row => string.Equals(row.HostId, host, StringComparison.Ordinal)))
            {
                rows.Add(new(host));
            }
        }
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>各PCの取得先を分離し、同時照会数を制限して候補を更新します。</summary>
    /// <returns>すべての行の照会完了です。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        loading = true;
        try
        {
            await Connection.RefreshAsync(cancellationToken).ConfigureAwait(true);
            using SemaphoreSlim parallelism = new(4);
            await Task.WhenAll(rows.Select(async row =>
            {
                await parallelism.WaitAsync(cancellationToken).ConfigureAwait(true);
                try
                {
                    row.Choices = await Choices.GetForHostAsync(row.HostId, true, cancellationToken).ConfigureAwait(true);
                    if (row.Choices.Problem is not null)
                    {
                        row.Manual = true;
                    }
                }
                finally
                {
                    parallelism.Release();
                }
            })).ConfigureAwait(true);
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>NIC変更後に別NICのIPを黙って残さず、IPを再選択させます。</summary>
    private static void SelectInterface(BindingRow row, string interfaceId)
    {
        if (!string.Equals(row.InterfaceId, interfaceId, StringComparison.Ordinal))
        {
            row.InterfaceId = interfaceId;
            row.LocalAddress = string.Empty;
        }
    }

    /// <summary>未検出PCや実行候補の将来追加にも、明示したHost識別子を保存できます。</summary>
    /// <returns>追加PCの候補取得完了です。</returns>
    private async Task AddHostAsync(CancellationToken cancellationToken = default)
    {
        string host = additionalHost.Trim();
        if (!CanEdit || string.IsNullOrWhiteSpace(host) || rows.Any(row => string.Equals(row.HostId, host, StringComparison.Ordinal)) || 256 <= rows.Count)
        {
            return;
        }
        rows.Add(new(host));
        additionalHost = string.Empty;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>検証できた割り当てだけをまとめて反映し、他PCの設定を混ぜません。</summary>
    /// <returns>編集モデルへの反映完了です。</returns>
    private async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanEdit || loading || invalidSaved)
        {
            return;
        }
        HostNetworkBinding[] bindings = rows.Where(row => row.Assigned).Select(row => new HostNetworkBinding(row.HostId, row.InterfaceId, row.LocalAddress)).OrderBy(row => row.HostId, StringComparer.Ordinal).ToArray();
        JsonElement settings = JsonSerializer.SerializeToElement(new { networkBindingMode = NetworkBindingSettings.PerHostMode, networkBindings = bindings }, ContractJson.Options);
        IReadOnlyList<Contracts.Configuration.ValidationIssue> issues = NetworkBindingSettings.Validate(new(NodeId, "network", settings));
        problem = null;
        if (issues.Count != 0)
        {
            problem = issues[0].Message;
        }
        if (problem is not null)
        {
            return;
        }
        await ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(bindings, ContractJson.Options)).ConfigureAwait(true);
        open = false;
    }

    /// <summary>一つのPCの編集中設定と、そのPCから照会した候補を保持します。</summary>
    private sealed record BindingRow(string hostId)
    {
        internal string HostId
        {
            get;
        } = hostId;

        internal bool Assigned
        {
            get; set;
        }

        internal bool Manual
        {
            get; set;
        }

        internal string InterfaceId
        {
            get; set;
        } = string.Empty;

        internal string LocalAddress
        {
            get; set;
        } = string.Empty;

        internal SettingNetworkChoices Choices
        {
            get; set;
        } = new(null, [], "Unverified");
    }
}
