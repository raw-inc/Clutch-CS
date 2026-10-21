using Microsoft.AspNetCore.Components;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;

/// <summary>ログが記録された接続先とWF・ノードを一緒に画面へ引き継ぎます。</summary>
internal readonly record struct EditorWorkflowLocation(string WorkflowId, string? NodeId = null, string? ClusterId = null, string? PeerId = null)
{
    private static string WorkflowKey => "workflow";

    /// <summary>識別子をURLの構造として解釈せず、クエリ値へ符号化します。</summary>
    /// <returns>WorkflowEditorへの相対URLです。</returns>
    internal string ToUri()
    {
        string uri = "/editor?workflow=" + Uri.EscapeDataString(WorkflowId);
        if (!string.IsNullOrEmpty(NodeId))
        {
            uri += "&node=" + Uri.EscapeDataString(NodeId);
        }
        if (!string.IsNullOrEmpty(ClusterId) && !string.IsNullOrEmpty(PeerId))
        {
            uri += "&cluster=" + Uri.EscapeDataString(ClusterId) + "&peer=" + Uri.EscapeDataString(PeerId);
        }
        return uri;
    }

    /// <summary>対象指定がない通常の画面遷移と、ログからの移動を区別します。</summary>
    /// <returns>WFを指定している場合だけ移動先を返します。</returns>
    internal static EditorWorkflowLocation? Read(string uri)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string pair in new Uri(uri).Query.TrimStart('?').Split('&'))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                values.TryAdd(parts[0], Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
            }
        }
        if (values.TryGetValue(WorkflowKey, out string? workflow) && !string.IsNullOrWhiteSpace(workflow))
        {
            return new(workflow, values.GetValueOrDefault("node"), values.GetValueOrDefault("cluster"), values.GetValueOrDefault("peer"));
        }
        return null;
    }

    /// <summary>読み込み済みの移動先を残さず、experimental等の別設定は維持します。</summary>
    /// <returns>移動先のクエリを除いたURLです。</returns>
    internal static string WithoutTarget(NavigationManager navigation) => navigation.GetUriWithQueryParameters(
        new Dictionary<string, object?> { ["workflow"] = null, ["node"] = null, ["cluster"] = null, ["peer"] = null });
}
