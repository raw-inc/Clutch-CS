namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>明示したプレビューノードの一時取得結果です。本文は別の分割読取りで取得します。</summary>
public sealed record NodePreviewSnapshot(Guid Id, string WorkflowId, string Revision, string NodeId, string PeerId,
    DateTimeOffset CapturedAt, string MediaType, int Length, bool Truncated = false, string? Problem = null)
{
    /// <summary>実行ごとに独立した進捗の識別です。従来Previewには付与しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProgressSession? Session
    {
        get; init;
    }

    /// <summary>小さい進捗値は一覧と一緒に取得し、更新ごとの本文読取りを避けます。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Values.ProgressValue? Progress
    {
        get; init;
    }
}
