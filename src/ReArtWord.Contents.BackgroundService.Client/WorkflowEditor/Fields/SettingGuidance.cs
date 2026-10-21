namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>混同しやすい設定の役割を、ノードの使用場面に合わせて説明します。</summary>
internal static class SettingGuidance
{
    /// <summary>同名でも異なる役割の設定名を区別します。</summary>
    /// <returns>通常の設定名を使う場合はnullです。</returns>
    internal static string? Label(string? type, string key) => (type, key) switch
    {
        ("subworkflow.input" or "subworkflow.output", "valueType") => "Public value type",
        ("subworkflow.input" or "subworkflow.output", "name") => "Public parameter name",
        ("control.log", "message") => "Message when no input is connected",
        ("control.concurrencyLimit", "maxConcurrency") => "Concurrent sessions",
        ("control.concurrencyLimit", "waitTimeoutMs") => "Queue timeout (ms)",
        ("control.concurrencyLimit", "timeoutMode") => "On queue timeout",
        ("ai.audio.qwen.generate", "voice") => "Voice ID (registered cloned voice)",
        ("value.constant", "type") => "Value type (unless connected)",
        ("value.type", "type") => "Type to define",
        ("value.file" or "value.directory", "peerId") => "Browse on peer",
        ("http.listen" or "tcp.listen" or "osc.tcp.listen", "timeoutMs") => "Per-request timeout (ms)",
        ("file.transfer", "path") => "Destination path on execution PC",
        ("file.transfer", "sourcePath") => "Source path on source host",
        ("http.request", "secretId") => "Saved authentication key name (optional in automatic mode)",
        (_, "processId") => "Registered application",
        (_, "secretRef" or "secretId") => "Saved API key name",
        (_, "localAddress" or "bindAddress") => "Local IPv4 on execution PC",
        (_, "remoteAddress") => "Destination IPv4",
        (_, "remotePort") => "Destination port",
        (_, "url") => "Request URL (destination endpoint)",
        _ => null,
    };

    /// <summary>選択先と結果の意味を、その場で確認できるようにします。</summary>
    /// <returns>追加説明、不要ならnullです。</returns>
    internal static string? Help(string? type, string key) => (type, key) switch
    {
        ("subworkflow.input" or "subworkflow.output", "defaultValue") => "SubWorkflow defaults must be JSON values matching the declared type. Disable the default to make the item required.",
        ("control.log", "message") => "Connect text or JSON values to the Message input above. Objects and arrays retain their structure; null is logged as null. This fallback setting accepts runtime text through its setting port.",
        (_, "localAddress" or "bindAddress") => "This is an IPv4 address assigned to the execution PC's selected network adapter. Outgoing requests use it as their source address; listeners bind here. The destination is configured separately.",
        (_, "interfaceId") => "Network adapter on the execution PC. Select the execution PC first, then choose its adapter and local IPv4 address.",
        ("http.request", "secretId") => "No key is needed for an unauthenticated endpoint. Select None under Authentication, or leave this name empty in Automatic mode. A saved name is required only when using secret authentication.",
        (_, "secretRef" or "secretId") => "A name registered in this cluster's secret store, not an environment variable or the key itself. Select a saved key or register one here.",
        ("file.transfer", "sourcePeer") => "The peer that owns the source file. The execution PC is the destination. Select either peer explicitly before browsing its paths.",
        ("file.transfer", "sourceHost") => "Select the source PC in this workflow's cluster. Connect PC selection to the execution PC input to choose the destination.",
        ("file.transfer", "requestId") => "Leave empty for a unique transfer ID for each execution. Set an ID only to explicitly resume the same source and content.",
        ("value.type", "type") => "This node builds a description of a type, not a data value. For array, connect one type definition to Element type. For object, build named fields with Key/value nodes whose values are type definitions, then connect the result to Property types.",
        ("value.constant", "text") => "The selected type converts this text into a value. Auto reads valid JSON and treats other input as text. A connected type definition overrides the type selector.",
        ("value.type", "optional") => "Allow this value to be missing or null when it is used as a field type.",
        (_, "count") => "Number of received items before normal completion. Unset (or 0 where allowed) keeps listening; this is separate from the per-item deadline.",
        _ => null,
    };
}
