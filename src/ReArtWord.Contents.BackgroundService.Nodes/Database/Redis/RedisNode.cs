using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Communication.Network;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Secrets;

namespace ReArtWord.Contents.BackgroundService.Nodes.Database.Redis;

/// <summary>明示接続から一つのキー操作を行い、送信後の不明な操作を再試行しません。</summary>
internal sealed class RedisNode(ISecretStore secrets) : IActionNode
{
    private static readonly UTF8Encoding utf8 = new(false, true);

    /// <summary>接続先・値形式・期限と対応するキー操作です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = new("database.redis", "Redis", "Database", "Read or write one external Redis key without implicit retry.", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
        [new("key", EPortValueKind.Json, true), new("value")], [new("value"), new("exists", EPortValueKind.Json), new("affected", EPortValueKind.Json)], ["success"], NetworkBindingSettings.Schema(NodeSettings.Schema("""
        {"type":"object","required":["interfaceId","localAddress","remoteAddress","remotePort","operation"],"properties":{
        "interfaceId":{"type":"string"},"localAddress":{"type":"string"},"localPort":{"type":"integer","x-runtime-input":true,"minimum":0,"maximum":65535,"default":0},"remoteAddress":{"type":"string","x-runtime-input":true},"remotePort":{"type":"integer","x-runtime-input":true,"minimum":1,"maximum":65535,"default":6379},
        "operation":{"type":"string","enum":["get","set","delete","exists"]},"format":{"type":"string","enum":["text","json","binary"],"default":"text"},
        "secretId":{"type":"string"},"username":{"type":"string"},"database":{"type":"integer","minimum":0,"maximum":2147483647,"default":0},
        "tls":{"type":"boolean"},"serverName":{"type":"string"},"ttlMs":{"type":"integer","minimum":0,"maximum":2147483647,"default":0},
        "timeoutMs":{"type":["integer","null"],"minimum":0,"maximum":2147483646,"default":30000},"maxBytes":{"type":"integer","minimum":1,"maximum":16777216,"default":1048576}}}
        """))) { HasAsyncCompletion = true };

    /// <summary>対象NICとTLSの検証名を確認します。</summary>
    /// <returns>開始前の設定問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor), .. CommunicationSupport.Validate(definition, true)];
        if (issues.Count != 0)
        {
            return issues;
        }
        NodeSettings settings = new(definition.Settings);
        bool tls = settings.ReadBoolean("tls");
        string? name = settings.ReadText("serverName");
        if (tls && string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new("redis.serverNameRequired", "TLS証明書を検証するserverNameが必要です。", definition.Id));
        }
        string? username = settings.ReadText("username");
        string? secretId = settings.ReadText("secretId");
        if (!string.IsNullOrEmpty(username) && string.IsNullOrWhiteSpace(secretId))
        {
            issues.Add(new("redis.secretRequired", "ACLユーザーのパスワードを参照するsecretIdが必要です。", definition.Id));
        }
        return issues;
    }

    /// <summary>認証・DB選択後、指定された業務操作を一回だけ送信します。</summary>
    /// <returns>存在と値、または確認できた失敗境界です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (NodeSupport.Invalid(context, this) is { } invalid)
        {
            return invalid;
        }
        bool sent = false;
        try
        {
            NodeSettings settings = new(context.Settings);
            int maximum = settings.ReadInt32("maxBytes", 1048576);
            string operation = settings.ReadText("operation")!;
            string format = settings.ReadText("format", "text")!;
            byte[][] arguments = Arguments(context, settings, operation, format, maximum);
            using CancellationTokenSource deadline = CommunicationSupport.Deadline(settings, cancellationToken);
            LocalNetworkBinding binding = new(context.Settings, context.HostId);
            using TcpClient client = new(new IPEndPoint(binding.Resolve(), settings.ReadInt32("localPort")));
            await client.ConnectAsync(IPAddress.Parse(settings.ReadText("remoteAddress")!), settings.ReadInt32("remotePort"), deadline.Token).ConfigureAwait(false);
            using NetworkStream network = client.GetStream();
            using SslStream secure = new(network, true);
            Stream stream = network;
            bool tls = settings.ReadBoolean("tls");
            if (tls)
            {
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.ReadText("serverName")! }, deadline.Token).ConfigureAwait(false);
                stream = secure;
            }
            bool ready = await PrepareAsync(stream, settings, deadline.Token).ConfigureAwait(false);
            if (!ready)
            {
                return NodeResult.Failure("redis.connectionRejected", "Redisが認証またはDB選択を拒否しました。", EOutcome.NotExecuted);
            }
            sent = true;
            await RedisProtocol.WriteAsync(stream, arguments, deadline.Token).ConfigureAwait(false);
            (bool error, byte[]? bytes, long? number) = await RedisProtocol.ReadAsync(stream, maximum, deadline.Token).ConfigureAwait(false);
            if (error)
            {
                return NodeResult.Failure("redis.commandRejected", "Redisがキー操作を拒否しました。", EOutcome.Failed);
            }
            return Result(operation, format, bytes, number);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or AuthenticationException or InvalidOperationException
            or ArgumentException or FormatException or JsonException or OverflowException or OperationCanceledException)
        {
            EOutcome outcome = EOutcome.NotExecuted;
            if (sent)
            {
                outcome = EOutcome.Unknown;
            }
            return NodeResult.Failure("redis.operationUnconfirmed", "Redis処理の完了を確認できません。接続秘密値やキー値は診断へ出力しません。", outcome);
        }
    }

    /// <summary>接続上の認証とDB選択は業務キー操作と分離します。</summary>
    /// <returns>業務操作を開始できる接続かです。</returns>
    private async Task<bool> PrepareAsync(Stream stream, NodeSettings settings, CancellationToken cancellationToken = default)
    {
        string? secretId = settings.ReadText("secretId");
        if (!string.IsNullOrWhiteSpace(secretId))
        {
            string password = await secrets.GetAsync(secretId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("redis.secretUnavailable");
            List<byte[]> auth = ["AUTH"u8.ToArray()];
            string? username = settings.ReadText("username");
            if (!string.IsNullOrEmpty(username))
            {
                auth.Add(utf8.GetBytes(username));
            }
            auth.Add(utf8.GetBytes(password));
            if (!await SetupAsync(stream, auth, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        int database = settings.ReadInt32("database");
        if (database != 0)
        {
            return await SetupAsync(stream, ["SELECT"u8.ToArray(), utf8.GetBytes(database.ToString(CultureInfo.InvariantCulture))], cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>認証結果を公開せず、明示OKのみ受け付けます。</summary>
    /// <returns>コマンドが受理されたかです。</returns>
    private static async Task<bool> SetupAsync(Stream stream, IReadOnlyList<byte[]> arguments, CancellationToken cancellationToken = default)
    {
        await RedisProtocol.WriteAsync(stream, arguments, cancellationToken).ConfigureAwait(false);
        (bool error, byte[]? bytes, long? number) = await RedisProtocol.ReadAsync(stream, 4096, cancellationToken).ConfigureAwait(false);
        return !error && number is null && bytes is not null && bytes.AsSpan().SequenceEqual("OK"u8);
    }

    /// <summary>必要な入力を送信前に検証し、一つのキーと有限の値へ限定します。</summary>
    /// <returns>対応コマンドの引数です。</returns>
    private static byte[][] Arguments(NodeExecutionContext context, NodeSettings settings, string operation, string format, int maximum)
    {
        string key = NodeSupport.Input(context, "key")?.GetString() ?? throw new FormatException("redis.keyRequired");
        string command = operation switch { "get" => "GET", "set" => "SET", "delete" => "DEL", "exists" => "EXISTS", _ => throw new FormatException("redis.operationUnsupported") };
        List<byte[]> arguments = [utf8.GetBytes(command), utf8.GetBytes(key)];
        string set = "set";
        if (string.Equals(operation, set, StringComparison.Ordinal))
        {
            NodeValue value = NodeSupport.Input(context, "value") ?? throw new FormatException("redis.valueRequired");
            byte[] bytes = format switch
            {
                "binary" when value.Kind == EValueKind.Binary => value.Binary.ToArray(),
                "text" => utf8.GetBytes(value.GetString() ?? throw new FormatException("redis.textRequired")),
                "json" when value.Kind == EValueKind.Json => utf8.GetBytes(value.Json.GetRawText()),
                _ => throw new FormatException("redis.valueTypeInvalid"),
            };
            arguments.Add(bytes);
            int ttl = settings.ReadInt32("ttlMs");
            if (0 < ttl)
            {
                arguments.Add("PX"u8.ToArray());
                arguments.Add(utf8.GetBytes(ttl.ToString(CultureInfo.InvariantCulture)));
            }
        }
        if (maximum < arguments.Sum(argument => (long)argument.Length))
        {
            throw new FormatException("redis.requestLimit");
        }
        return arguments.ToArray();
    }

    /// <summary>欠落キーと空値を区別し、要求に対応する応答だけを公開します。</summary>
    /// <returns>ワークフローの型付き結果です。</returns>
    private static NodeResult Result(string operation, string format, byte[]? bytes, long? number)
    {
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal);
        switch (operation)
        {
            case "get" when number is null:
                outputs["exists"] = NodeValue.FromObject(bytes is not null);
                if (bytes is not null)
                {
                    outputs["value"] = format switch
                    {
                        "binary" => NodeValue.FromBytes(bytes),
                        "json" => NodeValue.FromJson(JsonSerializer.Deserialize<JsonElement>(bytes)),
                        _ => NodeValue.FromObject(utf8.GetString(bytes)),
                    };
                }
                break;
            case "set" when bytes is not null && bytes.AsSpan().SequenceEqual("OK"u8) && number is null:
                outputs["affected"] = NodeValue.FromObject(1);
                break;
            case "delete" when number is 0 or 1 && bytes is null:
                outputs["affected"] = NodeValue.FromObject(number);
                break;
            case "exists" when number is 0 or 1 && bytes is null:
                outputs["exists"] = NodeValue.FromObject(number == 1);
                break;
            default:
                throw new InvalidDataException("redis.responseMismatch");
        }
        return new(EOutcome.Succeeded, "success", outputs);
    }
}
