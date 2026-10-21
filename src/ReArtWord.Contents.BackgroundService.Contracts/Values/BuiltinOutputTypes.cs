namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>設定や実行結果に依存しない、組込みノードの出力の種類です。</summary>
public static class BuiltinOutputTypes
{
    /// <summary>値を出力する場合に保証される型を返します。出力自体の有無は保証しません。</summary>
    /// <returns>組込みノードの既知の型、または未確定です。</returns>
    public static ValueTypeDefinition? Get(string nodeType, string port)
    {
        string value = "value";
        if (!string.Equals(port, value, StringComparison.Ordinal))
        {
            return Auxiliary(nodeType, port);
        }
        return nodeType switch
        {
            "execution.pc.select" or "execution.pc.lowest" => new(Targets.PcSelectionPorts.TypeName),
            "text.substring" => new("string"),
            "text.match" or "control.if" => new("boolean"),
            "text.regex" or "osc.packet" or "osc.parse" => new("object"),
            "value.type" => new("type definition"),
            "value.keyValue" or "data.toml.parse" => new("object"),
            "value.array" or "control.forEach.end" or "data.join" or "data.group" or "data.flatten" or "data.expand" => new("array"),
            "data.csv.parse" => new("array", new("array", new("string"))),
            "value.file" or "value.directory" => new("path"),
            "value.interface" or "value.peer" or "http.method" or "ai.text.generate"
                or "data.text.parse" or "data.text.serialize" or "data.json.serialize"
                or "data.yaml.serialize" or "data.toml.serialize" or "data.csv.serialize" => new("string"),
            "data.binary.serialize" or "file.read" or "tcp.listen" or "udp.receive" => new("binary"),
            "file.exists" or "directory.exists" or "math.compare" or "math.equal" or "math.notEqual" => new("boolean"),
            "math.add" or "math.subtract" or "math.multiply" or "math.divide" or "math.power" or "math.root" => new("number"),
            "directory.list" => new("array", new("object")),
            "file.append" or "file.copy" or "file.create" or "file.delete" or "file.move"
                or "file.rename" or "file.replace" or "file.upsert" or "file.transfer" or "file.watch"
                or "directory.copy" or "directory.create" or "directory.delete" or "directory.move" or "directory.rename"
                or "http.listen" or "http.request" or "http.reply" or "tcp.reply" or "websocket.reply"
                or "udp.send" or "osc.udp.send" or "artnet.send"
                or "osc.tcp.listen" or "osc.tcp.request" or "osc.udp.receive" or "artnet.receive"
                or "process.confirmRecovery" or "process.observe" or "process.recover" or "process.recoverySuppression"
                or "process.signal" or "process.start" or "process.stop" or "process.waitReady" or "process.watch"
                or "resource.observe" or "resource.watch" or "service.started"
                or "media.audio.convert" or "media.picture.convert" or "media.video.convert"
                or "ai.audio.generate" or "ai.audio.elevenlabs.generate" or "ai.audio.elevenlabs.v4.generate" or "ai.audio.qwen.audio3.generate" or "ai.audio.qwen.generate"
                or "ai.picture.generate" or "ai.video.generate" => new("object"),
            _ => null,
        };
    }

    /// <summary>値以外の出力も、同名の拡張ポートへ推測を広げずに分類します。</summary>
    /// <returns>組込みノードで確認できた補助出力の型です。</returns>
    private static ValueTypeDefinition? Auxiliary(string nodeType, string port) => (nodeType, port) switch
    {
        ("control.toggleButton", "isOn") => new("boolean"),
        ("text.regex", "count") => new("integer"),
        ("control.ifStart" or "control.ifNotStart", "start") => new("boolean"),
        ("control.if", "trueStart" or "falseStart") => new("boolean"),
        ("http.listen", "bodyContentType") => new("string"),
        ("database.sqlite" or "database.postgresql" or "database.redis", "affected") => new("integer"),
        ("database.redis", "exists") => new("boolean"),
        ("data.split", "matched" or "rejected" or "indeterminate") => new("array"),
        ("websocket.listen", "request") => new("object"),
        ("websocket.listen" or "websocket.request", "messageType") => new("string"),
        ("file.transfer", "requestId") => new("string"),
        ("osc.udp.receive" or "osc.tcp.listen" or "osc.tcp.request" or "osc.parse", "address") => new("string"),
        ("osc.udp.receive" or "osc.tcp.listen" or "osc.tcp.request" or "osc.parse", "arguments") => new("array"),
        ("ai.destination.route", "profile") => new("string"),
        ("ai.content.route", "classification") => new("string", Optional: true),
        ("ai.content.route", "coverage") or ("ai.video.generate", "job") => new("object"),
        ("data.extract", "count") or ("control.forEach" or "control.parallelForEach", "index" or "count") => new("integer"),
        ("http.listen" or "tcp.listen" or "osc.tcp.listen", "request") => new("object"),
        ("control.hook", "event") or ("process.watch", "recovery") => new("object"),
        ("resource.observe" or "resource.watch", "metrics") => new("object"),
        ("scheduling.timer", "scheduledAt" or "observedAt") => new("string"),
        ("control.condition", "matched") => new("boolean", Optional: true),
        ("control.condition", "reason") => new("object"),
        ("state.get" or "state.set" or "state.delete" or "state.compareExchange" or "state.dedupe", "version") => new("string", Optional: true),
        ("state.get" or "state.set" or "state.delete" or "state.compareExchange" or "state.dedupe", "applied" or "exists" or "localOnly") => new("boolean"),
        ("state.get" or "state.set", "availability") => new("object"),
        ("state.get" or "state.set", "persistenceOutcome") => new("integer"),
        _ => null,
    };
}
