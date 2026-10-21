using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Inspection;

/// <summary>選択したノードの設定と実行先を編集します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class NodeInspector
{
    /// <summary>詳細欄からの押下もキャンバスと同じ手動実行へ渡します。</summary>
    [Parameter]
    public EventCallback<string> ButtonPressed
    {
        get; set;
    }

    /// <summary>選択したトグルについて親画面が確認した状態です。</summary>
    [Parameter]
    public bool? ToggleState
    {
        get; set;
    }

    /// <summary>閉じている詳細パネルの入力欄を再構築しません。</summary>
    [Parameter]
    public bool Visible
    {
        get; set;
    } = true;

    private static string MatchCoordinator => "coordinator";
    private static string MatchSource => "source";
    private static string MatchGroups => "groups";

    /// <summary>無期限と正の待受時間を区別し、不正入力を保存しません。</summary>
    private void ListenDurationChanged(ChangeEventArgs args)
    {
        if (Node is null)
        {
            return;
        }
        string raw = args.Value?.ToString() ?? string.Empty;
        string key = Node.Id + "/listenDurationMs";
        if (raw.Length == 0)
        {
            Model.SetInput(key, null);
            Update(node => node with { ListenDurationMs = null });
        }
        else
        {
            if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int milliseconds) && 0 < milliseconds)
            {
                Model.SetInput(key, null);
                Update(node => node with { ListenDurationMs = milliseconds });
            }
            else
            {
                Model.SetInput(key, raw);
            }
        }
    }

    [Inject]
    private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    /// <summary>編集に使用するノード定義の一覧です。</summary>
    // 抑制理由: Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。
    [Parameter, System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "Blazorが描画ごとにParameterを設定するため、受信するコレクション参照のsetterを維持します。")]
    public IReadOnlyList<NodeDescriptor> Catalog
    {
        get;
        set;
    } = [];

    /// <summary>入力内容の検証不能状態を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<bool> InvalidChanged
    {
        get;
        set;
    }

    private NodeDefinition? Node => Model.Draft.Document.Nodes.FirstOrDefault(node => string.Equals(node.Id, Model.SelectedNode, StringComparison.Ordinal));
    private NodeDescriptor? Descriptor => Presentation.Graph(Model.Draft.Document, Catalog).EditorDescriptor(Node?.Id ?? string.Empty);

    /// <summary>期待している旧版も残し、未対応の設定を別の版へ勝手に変更しません。</summary>
    private IEnumerable<int> ContractVersions => (Descriptor?.CompatibleContractVersions ?? []).Append(Descriptor?.ContractVersion ?? 1)
        .Append(Node?.ContractVersion ?? 1).Distinct().Order();

    /// <summary>利用者が選んだ契約だけを変更します。設定値と接続は変換しません。</summary>
    private void ContractChanged(ChangeEventArgs change)
    {
        string? text = change.Value?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            Update(node => node with { ContractVersion = null });
        }
        else
        {
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int version) && 0 < version)
            {
                Update(node => node with { ContractVersion = version });
            }
        }
    }

    private string TargetMode => Node?.Target is null ? "coordinator"
                                                      : Node.Target.SourcePeer ? "source"
                                                                               : Node.Target.Scope switch
    {
        EExecutionScope.One => "one",
        EExecutionScope.Any => "any",
        EExecutionScope.All => "all",
        _ => throw new InvalidOperationException("Unknown execution scope."),
    };

    /// <summary>選択ノードがある場合だけ指定した編集を反映します。</summary>
    private void Update(Func<NodeDefinition, NodeDefinition> edit)
    {
        if (Node is not null)
        {
            Model.ChangeNode(Node.Id, edit);
        }
    }

    /// <summary>ノードの実行先モードを選択値へ変更します。</summary>
    private void TargetChanged(ChangeEventArgs e)
    {
        string? mode = e.Value?.ToString();
        Update(n => n with { Target = string.Equals(mode, MatchCoordinator, StringComparison.Ordinal) ? null
                                                                                                      : string.Equals(mode, MatchSource, StringComparison.Ordinal) ? new(EExecutionScope.One, SourcePeer: true)
                                                                                                                                                                   : new(Enum.Parse<EExecutionScope>(mode!, true), Peers: []) });
    }

    /// <summary>実行先の指定方式をピアまたはグループへ切り替えます。</summary>
    private void TargetKindChanged(ChangeEventArgs e)
    {
        Update(n => n with { Target = string.Equals(e.Value?.ToString(), MatchGroups, StringComparison.Ordinal) ? new(n.Target!.Scope, Groups: [])
                                                                                                                : new(n.Target!.Scope, Peers: []) });
    }

    /// <summary>入力された実行先IDを整形してノードの対象指定へ反映します。</summary>
    private void TargetsChanged(ChangeEventArgs e)
    {
        string[]? ids = (e.Value?.ToString() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Update(n => n with { Target = n.Target!.Groups is null ? n.Target with { Peers = ids }
                                                               : n.Target with { Groups = ids } });
    }

    /// <summary>停止中の入力保持方針を表示名へ変換します。</summary>
    /// <returns>破棄・最新のみ・全件保持に対応する表示キーです。</returns>
    private static string StoppedInputLabel(EStoppedInputPolicy policy) => policy switch
    {
        EStoppedInputPolicy.Drop => "Discard inputs",
        EStoppedInputPolicy.Latest => "Keep latest input",
        EStoppedInputPolicy.All => "Keep all inputs",
        _ => policy.ToString(),
    };

    /// <summary>未指定を区別してHTML入力用の真偽値を返します。</summary>
    /// <returns>true、false、または未指定を示す空文字列です。</returns>
    private static string NullableBoolean(bool? value) => value switch { true => "true", false => "false", null => string.Empty };

    /// <summary>入力された真偽値を未指定と区別して読み取ります。</summary>
    /// <returns>読み取った真偽値、真偽値でなければ未指定を示すnullです。</returns>
    private static bool? ReadBoolean(ChangeEventArgs e) => bool.TryParse(e.Value?.ToString(), out bool value) ? value
                                                                                                              : null;

    /// <summary>編集保護を確認して選択ノードを削除します。</summary>
    private void Remove()
    {
        if (Node is not null)
        {
            GraphDeletion.Node(Model, Node.Id);
        }
    }
}
