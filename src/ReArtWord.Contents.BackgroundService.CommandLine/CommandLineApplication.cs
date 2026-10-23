using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>管理API操作と明示したCoreの前景起動を提供します。</summary>
internal static class CommandLineApplication
{
    /// <summary>操作を一度だけ送信し、本文と終了コードで実行結果を返します。</summary>
    /// <returns>0確認済、2入力不正、3HTTP失敗、4業務非成功、5不明、6API未接続、130読取取消です。</returns>
    internal static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string service = "service";
        string run = "run";
        if (2 <= args.Length && string.Equals(args[0], service, StringComparison.Ordinal) && string.Equals(args[1], run, StringComparison.Ordinal))
        {
            return await ServiceRunner.RunAsync(args, output, error, cancellationToken).ConfigureAwait(false);
        }
        string help = "--help";
        if (args.Length == 1 && string.Equals(args[0], help, StringComparison.Ordinal))
        {
            await output.WriteLineAsync("Usage: --url http(s)://127.0.0.1:port [--cluster CLUSTER --peer PEER] [--display-name NAME] COMMAND [--id ID] [--body JSON_FILE|-] [--operation-id GUID]\nLocal ClientHost: network get|set --body binding.json; cluster list; request status --id GUID; fleet preview|start --body FILE; fleet list; fleet status|reconcile --id GUID. Core commands (including archive) require --cluster and --peer. Archive: archive export --file FILE [--logs true|false]; archive preview --file FILE [--mode merge|replace]; archive import --id GUID --confirmation TOKEN; archive status|reconcile --id GUID. Commands: service run --executable PATH --config TOML (no URL); configuration get|save|activate|impact|status; resources get|history; trace get|set|status; log statistics|retention|retention-set; script analyze --body FILE; workflow parse|validate --yaml FILE|-; status; catalog; conflict list|query|detail|resolve|status; workflow list|query|get|impact|definition-chunk|validate|save|activate|run|run-status|compatibility|running|running-query|cancel|cancel-status|operation|detached|detached-query|detached-detail|detached-resolve|detached-status; execution list|query; source stop; cluster detach|rejoin; secret set --name NAME (--stdin|--env ENV_NAME); snapshot list|query|capture|capture-status|restore|restore-status|recover; file operation|recover; safety list|set|status; update start|status|schedules|schedule-save|schedule-cancel|schedule-status|schedule-operation; log query; operation status\nExit: 0 confirmed, 2 input, 3 HTTP error, 4 failed/partial, 5 unknown/pending, 6 offline, 130 cancelled/read timeout. Detached resolution JSON requires expectedLocalRevision, expectedCurrentRevision (null allowed), and currentRevisionConfirmed:true. No automatic mutation retry.").ConfigureAwait(false);
            return 0;
        }
        CliCommand? command = null;
        bool sent = false;
        bool mutationSent = false;
        try
        {
            command = CliCommand.Parse(args);
            string archivePrefix = "archive ";
            if (command.Name.StartsWith(archivePrefix, StringComparison.Ordinal))
            {
                return await CliArchive.RunAsync(command, output, error, cancellationToken).ConfigureAwait(false);
            }
            JsonObject? body = await ReadBodyAsync(command, input, cancellationToken).ConfigureAwait(false);
            Guid? operationId = PrepareOperation(command, body);
            if (operationId is not null)
            {
                await error.WriteLineAsync("operationId=" + operationId.Value.ToString("D")).ConfigureAwait(false);
            }
            using SocketsHttpHandler handler = new() { AllowAutoRedirect = false, UseProxy = false };
            using HttpClient client = new(handler) { BaseAddress = command.BaseAddress, Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 16777216 };
            string managementPrefix = "/api/";
            if (command.Path.StartsWith(managementPrefix, StringComparison.Ordinal))
            {
                await VerifyClientHostAsync(client, cancellationToken).ConfigureAwait(false);
            }
            string validate = "workflow validate";
            string yamlOption = "--yaml";
            if (string.Equals(command.Name, validate, StringComparison.Ordinal) && command.Options.ContainsKey(yamlOption))
            {
                body = await CliYaml.ParseAsync(client, command, body!, cancellationToken).ConfigureAwait(false);
            }
            using HttpRequestMessage request = new(command.Method, command.Path);
            string displayNameOption = "--display-name";
            if (command.Options.TryGetValue(displayNameOption, out string? displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                request.Headers.Add("X-Contents-Display-Name", displayName);
            }
            Guid? requestId = null;
            string clusterOption = "--cluster";
            if (command.Options.TryGetValue(clusterOption, out string? cluster))
            {
                request.Headers.Add("X-Contents-Cluster", cluster);
                request.Headers.Add("X-Contents-Peer", command.Options["--peer"]);
            }
            if (command.Method != HttpMethod.Get)
            {
                requestId = operationId ?? Guid.NewGuid();
                request.Headers.Add("X-Contents-Request", requestId.Value.ToString("D"));
                await error.WriteLineAsync("requestId=" + requestId.Value.ToString("D")).ConfigureAwait(false);
            }
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            sent = true;
            mutationSent = command.Mutation;
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            int httpStatus = (int)response.StatusCode;
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonElement value = ParseResponse(text);
            int code = response.IsSuccessStatusCode ? CliResponse.Classify(command.Name, value, httpStatus)
                                                    : 3;
            if (operationId is not null && !CliResponse.MatchesOperation(value, operationId.Value))
            {
                code = 5;
            }
            object? visible = value.ValueKind == JsonValueKind.Undefined ? null
                                                                         : value;
            if (command.Secret)
            {
                visible = new { responseHidden = true };
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new { operationId, requestId, httpStatus, exitCode = code, response = visible })).ConfigureAwait(false);
            return code;
        }
        catch (OperationCanceledException)
        {
            int code = mutationSent ? 5
                                    : 130;
            await error.WriteLineAsync(code == 5 ? "request outcome unknown; query the same operation ID; no automatic retry"
                                                 : "request cancelled or timed out").ConfigureAwait(false);
            return code;
        }
        catch (HttpRequestException exception)
        {
            if (exception.StatusCode is not null)
            {
                await error.WriteLineAsync("API rejected YAML parsing; validation not sent").ConfigureAwait(false);
                return 3;
            }
            int code = mutationSent ? 5
                                    : 6;
            await error.WriteLineAsync(code == 5 ? "API unavailable; mutation outcome unknown; query the same operation ID"
                                                 : "API unavailable").ConfigureAwait(false);
            return code;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            int code = sent ? 5
                            : 2;
            await error.WriteLineAsync(sent ? "API response could not be confirmed"
                                            : "invalid input; use a loopback ClientHost --url and explicit --cluster/--peer for Core commands; JSON bodies use --body file or --body -; secret set uses --name and --stdin or --env").ConfigureAwait(false);
            return code;
        }
    }

    /// <summary>CoreHTTPを直接操作せず、ローカルObserver facadeであることを確認します。</summary>
    /// <returns>クライアント自身のNIC APIの確認です。</returns>
    internal static async Task VerifyClientHostAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri("/client/network", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement state = ParseResponse(text);
        string interfaces = "interfaces";
        string listening = "listening";
        if (!response.IsSuccessStatusCode || state.ValueKind != JsonValueKind.Object || !state.TryGetProperty(interfaces, out JsonElement adapters)
            || adapters.ValueKind != JsonValueKind.Array || !state.TryGetProperty(listening, out JsonElement active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException("specified URL is not a local ClientHost");
        }
    }

    /// <summary>API本文を入力から取得します。秘密をコマンド引数へ許可しません。</summary>
    /// <returns>送信用JSON、または本文なしです。</returns>
    private static async Task<JsonObject?> ReadBodyAsync(CliCommand command, TextReader input, CancellationToken cancellationToken = default)
    {
        if (command.Secret)
        {
            bool stdin = command.Options.ContainsKey("--stdin");
            bool environment = command.Options.TryGetValue("--env", out string? variable);
            if (stdin == environment)
            {
                throw new ArgumentException("select one secret source", nameof(command));
            }
            string secret = stdin ? await ReadBoundedAsync(input, 65536, cancellationToken).ConfigureAwait(false)
                                  : Environment.GetEnvironmentVariable(variable!) ?? throw new ArgumentException("missing secret environment", nameof(command));
            string name = command.Options["--name"];
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(secret);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(secret.Length, 65536);
            return new JsonObject { ["name"] = name, ["value"] = secret };
        }
        string keyCluster = "cluster ";
        string fileRecover = "file recover";
        string fleetReconcile = "fleet reconcile";
        if (string.Equals(command.Name, fleetReconcile, StringComparison.Ordinal) || command.Method == HttpMethod.Get || string.Equals(command.Name, fileRecover, StringComparison.Ordinal) || command.Name.StartsWith(keyCluster, StringComparison.Ordinal))
        {
            return null;
        }
        bool yaml = command.Options.TryGetValue("--yaml", out string? yamlSource);
        string source = yaml ? yamlSource!
                             : command.Options["--body"];
        string dash = "-";
        string text;
        if (string.Equals(source, dash, StringComparison.Ordinal))
        {
            text = await ReadBoundedAsync(input, 16777216, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using StreamReader reader = new(source, Encoding.UTF8, true);
            text = await ReadBoundedAsync(reader, 16777216, cancellationToken).ConfigureAwait(false);
        }
        if (yaml)
        {
            return new JsonObject { ["text"] = text };
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("JSON object required", nameof(command));
    }

    /// <summary>再照会できるIDを本文に固定します。</summary>
    /// <returns>送信する操作IDです。</returns>
    private static Guid? PrepareOperation(CliCommand command, JsonObject? body)
    {
        if (!command.Operation)
        {
            string identityOption = "--id";
            string workflowGet = "workflow get";
            string detachedDetail = "workflow detached-detail";
            if (!string.Equals(command.Name, workflowGet, StringComparison.Ordinal) && !string.Equals(command.Name, detachedDetail, StringComparison.Ordinal) && command.Options.TryGetValue(identityOption, out string? identity))
            {
                return Guid.Parse(identity);
            }
            return null;
        }
        string keyOperationId3 = "--operation-id";
        Guid? supplied = command.Options.TryGetValue(keyOperationId3, out string? option) ? Guid.Parse(option)
                                                                                          : null;
        string keyOperationId4 = "operationId";
        Guid? existing = body![keyOperationId4] is JsonNode node ? Guid.Parse(node.GetValue<string>())
                                                                 : null;
        if (supplied is not null && existing is not null && supplied != existing)
        {
            throw new ArgumentException("operation ID mismatch", nameof(command));
        }
        string recover = "snapshot recover";
        if (string.Equals(command.Name, recover, StringComparison.Ordinal) && existing is null)
        {
            throw new ArgumentException("original recovery operation ID required", nameof(command));
        }
        Guid selected = existing ?? supplied ?? Guid.NewGuid();
        if (selected == Guid.Empty)
        {
            throw new ArgumentException("empty operation ID", nameof(command));
        }
        body["operationId"] = selected.ToString("D");
        return selected;
    }

    /// <summary>入力の最大長を超える前に読取りを止めます。</summary>
    /// <returns>上限内の入力です。</returns>
    private static async Task<string> ReadBoundedAsync(TextReader input, int limit, CancellationToken cancellationToken = default)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (limit - count < text.Length)
            {
                throw new ArgumentException("input limit exceeded", nameof(input));
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    /// <summary>不正応答をそのまま端末へ流さずJSONだけを取得します。</summary>
    /// <returns>正常なJSONか未定義値です。</returns>
    private static JsonElement ParseResponse(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
