using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Values;

/// <summary>組込みノードの成功入力で分かるJSON区分だけを照合します。</summary>
public static class BuiltinInputTypes
{
    /// <summary>未接続の入力も、実装が受け取る型契約を表示します。</summary>
    /// <returns>複数種類を受ける入力は未確定です。</returns>
    public static ValueTypeDefinition? Expected(string nodeType, string port) => (nodeType, port) switch
    {
        ("control.button" or "control.toggleButton", "label") => new("string", Optional: true),
        ("text.substring" or "text.regex" or "text.match", "value") or ("text.regex" or "text.match", "pattern") => new("string"),
        ("text.substring", "startIndex") => new("integer"),
        ("text.substring", "length") => new("integer", Optional: true),
        ("value.constant", "type") or ("value.array" or "value.type" or "control.forEach" or "control.parallelForEach", "elementType") or ("http.listen", "queryType" or "bodyType") => new("type definition"),
        ("value.type", "properties") => new("object", Properties: null),
        ("value.keyValue", "base") or ("data.object.expand", "value") => new("object"),
        ("osc.packet", "packet") => new("object"),
        ("value.keyValue", "key") or ("value.comment", "displayName") or ("data.convert", "format") or ("http.listen", "method") => new("string"),
        ("data.concat", "separator") => new("string", Optional: true),
        ("value.array", "items") or ("math.ands" or "math.ors", "value") => new("array"),
        ("value.dictionary", "item") => new("object"),
        ("http.request", "method") or ("osc.udp.send" or "osc.tcp.request" or "osc.packet", "address") => new("string"),
        ("http.reply" or "tcp.reply" or "websocket.reply", "request") or ("osc.udp.send" or "osc.tcp.request" or "artnet.send", "value") => new("object"),
        ("scheduling.timer", "clockValid") => new("boolean"),
        ("control.ifStart" or "control.ifNotStart" or "control.if", "condition") => new("boolean"),
        ("database.sqlite" or "database.postgresql", "parameters") => new("object"),
        ("database.redis", "key") => new("string"),
        ("data.join", "left" or "right") or ("data.group" or "data.flatten" or "data.expand" or "data.split", "value") => new("array"),
        ("state.get" or "state.set" or "state.delete" or "state.compareExchange" or "state.dedupe", "key") => new("string"),
        ("state.compareExchange", "expectedVersion") => new("string", Optional: true),
        ("ai.text.generate" or "ai.picture.generate" or "ai.video.generate" or "ai.audio.generate" or "ai.audio.elevenlabs.generate" or "ai.audio.elevenlabs.v3.generate" or "ai.audio.elevenlabs.v4.generate" or "ai.audio.elevenlabs.soundEffects" or "ai.audio.qwen.audio3.generate" or "ai.audio.qwen.generate" or "ai.audio.qwen.tts.generate", "value") => new("string"),
        ("control.progress", "total" or "current") or ("math.add" or "math.subtract" or "math.multiply" or "math.divide" or "math.compare", "left" or "right")
            or ("math.power", "base" or "exponent") or ("math.root", "value" or "degree") => new("number"),
        _ => null,
    };

    /// <summary>範囲や整数性を値評価で推測せず、確定した型の不一致を検出します。</summary>
    /// <returns>未知・混在は許可し、実行時検証へ渡します。</returns>
    internal static bool Accepts(string nodeType, string port, ValueTypeDefinition? inferred, ValueTypeDefinition? companion = null)
    {
        string anyType = "any";
        string valuePort = "value";
        if (inferred is null || inferred.Optional || string.Equals(inferred.Type, anyType, StringComparison.Ordinal))
        {
            return true;
        }
        string actual = inferred.Type switch { "path" => "string", _ => inferred.Type };
        ValueTypeDefinition? expected = Expected(nodeType, port);
        if (expected is not null && !ValueTypeInference.CanAssign(expected, inferred))
        {
            return false;
        }
        if (IterationNodeTypes.IsStart(nodeType) && string.Equals(port, valuePort, StringComparison.Ordinal))
        {
            return actual is "array" or "object";
        }
        return (nodeType, port) switch
        {
            ("control.log", "message") => !string.Equals(actual, "binary", StringComparison.Ordinal),
            ("math.add" or "math.subtract" or "math.multiply" or "math.divide" or "math.compare", "left" or "right") => IsNumber(actual),
            ("math.power", "base" or "exponent") => IsNumber(actual),
            ("math.root", "value" or "degree") => IsNumber(actual),
            ("math.not", "value") => IsLogic(actual),
            ("math.and" or "math.or" or "math.nand" or "math.nor" or "math.xor", "left" or "right") => IsLogic(actual)
                && (companion is null || companion.Optional || string.Equals(companion.Type, anyType, StringComparison.Ordinal)
                    || ValueTypeInference.Same(ValueTypeInference.LogicResult(inferred), ValueTypeInference.LogicResult(companion))),
            ("math.ands" or "math.ors", "value") => string.Equals(actual, "array", StringComparison.Ordinal)
                && (inferred.ElementType is null || inferred.ElementType.Optional || string.Equals(inferred.ElementType.Type, "any", StringComparison.Ordinal) || IsLogic(inferred.ElementType.Type)),
            ("value.array", "items") => string.Equals(actual, "array", StringComparison.Ordinal),
            ("value.dictionary", "items") => actual is "array" or "object",
            ("data.keyValue.get", "value") => actual is "array" or "object",
            ("data.pick" or "data.randomPick" or "data.count", "value") => actual is "array" or "object",
            ("value.array", "item") or ("value.keyValue", "value") => !string.Equals(actual, "binary", StringComparison.Ordinal),
            ("value.keyValue", "key") => actual is "string" or "char",
            ("value.keyValue", "base") or ("value.type", "properties") => actual is "object" or "type definition",
            ("value.constant", "type") or ("value.array" or "value.type" or "control.forEach" or "control.parallelForEach", "elementType") or ("http.listen", "queryType" or "bodyType") => actual is "object" or "type definition",
            ("http.listen", "method") => actual is "string" or "char",
            ("data.convert", "value") => actual is "boolean" or "byte" or "integer" or "int64" or "number" or "decimal" or "char" or "string",
            ("data.json.parse" or "data.yaml.parse" or "data.toml.parse" or "data.csv.parse" or "data.text.parse", "value") => actual is "string" or "char" or "binary",
            ("data.binary.parse", "value") => string.Equals(actual, "binary", StringComparison.Ordinal),
            ("osc.parse", "value") => actual is "object" or "binary",
            ("data.json.serialize" or "data.yaml.serialize", "value") => !string.Equals(actual, "binary", StringComparison.Ordinal),
            ("data.text.serialize", "value") => actual is "string" or "char",
            ("data.toml.serialize", "value") => actual is "object" or "type definition",
            ("data.csv.serialize", "value") => string.Equals(actual, "array", StringComparison.Ordinal),
            ("media.audio.convert" or "media.picture.convert" or "media.video.convert", "value") => actual is "string" or "object",
            ("ai.content.route", "value") => actual is "string" or "object" or "binary",
            _ => true,
        };
    }

    /// <summary>既知の数値表現を同じJSON number区分として扱います。</summary>
    /// <returns>数値区分かどうかです。</returns>
    private static bool IsNumber(string type) => type is "byte" or "integer" or "int64" or "number" or "decimal";

    /// <summary>論理ノードの真偽値または数値を受け、正確な整数性は実行時に検証します。</summary>
    /// <returns>入力が成功する可能性を持つ型かです。</returns>
    private static bool IsLogic(string type) => string.Equals(type, "boolean", StringComparison.Ordinal) || IsNumber(type);
}
