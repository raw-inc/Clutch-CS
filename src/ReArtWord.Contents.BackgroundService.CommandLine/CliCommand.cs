namespace ReArtWord.Contents.BackgroundService.CommandLine;

/// <summary>許可された管理API操作と入力指定です。</summary>
internal sealed record CliCommand(Uri BaseAddress, string Name, string Path, HttpMethod Method,
    IReadOnlyDictionary<string, string> Options, bool Operation, bool Secret, bool Mutation)
{
    /// <summary>不明な引数を拒否し、実在する管理APIだけへ解決します。</summary>
    /// <returns>入力を伴わない操作計画です。</returns>
    internal static CliCommand Parse(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        List<string> words = [];
        for (int index = 0; index < args.Length; index++)
        {
            string word = args[index];
            string key = "--";
            if (word.StartsWith(key, StringComparison.Ordinal))
            {
                string stdin = "--stdin";
                string value = "true";
                if (!string.Equals(word, stdin, StringComparison.Ordinal))
                {
                    index++;
                    if (args.Length <= index)
                    {
                        throw new ArgumentException("option value missing", nameof(args));
                    }
                    value = args[index];
                }
                options.Add(word, value);
            }
            else
            {
                words.Add(word);
            }
        }
        string name = string.Join(' ', words);
        (string path, HttpMethod method, bool operation, bool secret, bool mutation) = name switch
        {
            "conflict query" => ("/api/conflicts/query", HttpMethod.Post, false, false, false),
            "conflict detail" => ("/api/conflicts/detail", HttpMethod.Post, false, false, false),
            "conflict list" => ("/api/conflicts", HttpMethod.Get, false, false, false),
            "conflict resolve" => ("/api/conflicts/resolve", HttpMethod.Post, true, false, true),
            "conflict status" => ("/api/conflicts/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "configuration get" => ("/api/configuration", HttpMethod.Get, false, false, false),
            "configuration save" => ("/api/configuration/save", HttpMethod.Post, true, false, true),
            "configuration activate" => ("/api/configuration/activate", HttpMethod.Post, true, false, true),
            "configuration impact" => ("/api/configuration/impact", HttpMethod.Get, false, false, false),
            "configuration status" => ("/api/configuration/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "resources get" => ("/api/resources", HttpMethod.Get, false, false, false),
            "resources history" => ("/api/resources/history", HttpMethod.Get, false, false, false),
            "trace get" => ("/api/trace", HttpMethod.Get, false, false, false),
            "trace set" => ("/api/trace", HttpMethod.Post, true, false, true),
            "trace status" => ("/api/trace/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "log statistics" => ("/api/logs/statistics", HttpMethod.Get, false, false, false),
            "log retention" => ("/api/logs/retention", HttpMethod.Get, false, false, false),
            "log retention-set" => ("/api/logs/retention", HttpMethod.Post, true, false, true),
            "script analyze" => ("/api/scripts/analyze", HttpMethod.Post, false, false, false),
            "workflow parse" => ("/api/workflows/parse", HttpMethod.Post, false, false, false),
            "status" => ("/api/status", HttpMethod.Get, false, false, false),
            "catalog" => ("/api/catalog", HttpMethod.Get, false, false, false),
            "workflow detached" => ("/api/workflows/detached", HttpMethod.Get, false, false, false),
            "workflow detached-query" => ("/api/workflows/detached/query", HttpMethod.Post, false, false, false),
            "workflow detached-detail" => ("/api/workflows/" + Identifier(options) + "/detached", HttpMethod.Get, false, false, false),
            "workflow detached-resolve" => ("/api/workflows/detached/resolve", HttpMethod.Post, true, false, true),
            "workflow detached-status" => ("/api/workflows/detached/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "workflow list" => ("/api/workflows", HttpMethod.Get, false, false, false),
            "workflow query" => ("/api/workflows/query", HttpMethod.Post, false, false, false),
            "workflow definition-chunk" => ("/api/workflows/definition-chunk", HttpMethod.Post, false, false, false),
            "execution list" => ("/api/executions", HttpMethod.Get, false, false, false),
            "execution query" => ("/api/executions/query", HttpMethod.Post, false, false, false),
            "workflow get" => ("/api/workflows/" + Identifier(options), HttpMethod.Get, false, false, false),
            "workflow impact" => ("/api/workflows/" + Identifier(options) + "/impact", HttpMethod.Get, false, false, false),
            "workflow validate" => ("/api/workflows/validate", HttpMethod.Post, false, false, false),
            "workflow compatibility" => ("/api/workflows/compatibility", HttpMethod.Post, false, false, false),
            "workflow save" => ("/api/workflows/save", HttpMethod.Post, true, false, true),
            "workflow activate" => ("/api/workflows/activate", HttpMethod.Post, true, false, true),
            "workflow run" => ("/api/runs", HttpMethod.Post, true, false, true),
            "workflow running" => ("/api/runs/active", HttpMethod.Get, false, false, false),
            "workflow running-query" => ("/api/runs/active/query", HttpMethod.Post, false, false, false),
            "workflow run-status" or "run-status" => ("/api/runs/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "workflow operation" => ("/api/workflows/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "workflow cancel" => ("/api/runs/cancel", HttpMethod.Post, true, false, true),
            "workflow cancel-status" => ("/api/runs/cancellations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "operation status" => ("/api/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "source stop" => ("/api/sources/stop", HttpMethod.Post, false, false, true),
            "cluster detach" => ("/api/cluster/detach", HttpMethod.Post, false, false, true),
            "cluster rejoin" => ("/api/cluster/rejoin", HttpMethod.Post, false, false, true),
            "secret set" => ("/api/secrets", HttpMethod.Put, false, true, true),
            "snapshot list" => ("/api/snapshots", HttpMethod.Get, false, false, false),
            "snapshot query" => ("/api/snapshots/query", HttpMethod.Post, false, false, false),
            "snapshot capture" => ("/api/snapshots/capture", HttpMethod.Post, true, false, true),
            "snapshot restore" => ("/api/snapshots/restore", HttpMethod.Post, true, false, true),
            "snapshot capture-status" => ("/api/snapshots/captures/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "snapshot restore-status" => ("/api/snapshots/restores/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "snapshot recover" => ("/api/snapshots/recover", HttpMethod.Post, true, false, true),
            "file operation" => ("/api/files/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "file recover" => ("/api/files/operations/" + OperationIdentifier(options) + "/recover", HttpMethod.Post, false, false, true),
            "safety list" => ("/api/safety", HttpMethod.Get, false, false, false),
            "safety set" => ("/api/safety", HttpMethod.Post, true, false, true),
            "safety status" => ("/api/safety/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "update start" => ("/api/updates/start", HttpMethod.Post, true, false, true),
            "update status" => ("/api/updates/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "update schedules" => ("/api/updates/schedules", HttpMethod.Get, false, false, false),
            "update schedule-save" => ("/api/updates/schedules/save", HttpMethod.Post, true, false, true),
            "update schedule-cancel" => ("/api/updates/schedules/cancel", HttpMethod.Post, true, false, true),
            "update schedule-status" => ("/api/updates/schedules/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "update schedule-operation" => ("/api/updates/schedules/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "archive export" => ("/api/archive/export", HttpMethod.Post, false, false, false),
            "archive preview" => ("/api/archive/preview", HttpMethod.Post, false, false, false),
            "archive import" => ("/api/archive/import", HttpMethod.Post, false, false, true),
            "archive status" => ("/api/archive/operations/" + OperationIdentifier(options), HttpMethod.Get, false, false, false),
            "archive reconcile" => ("/api/archive/operations/" + OperationIdentifier(options) + "/reconcile", HttpMethod.Post, false, false, true),
            "network get" => ("/client/network", HttpMethod.Get, false, false, false),
            "network set" => ("/client/network", HttpMethod.Post, false, false, true),
            "cluster list" => ("/client/clusters", HttpMethod.Get, false, false, false),
            "request status" => ("/client/commands/" + OperationIdentifier(options) + "/result", HttpMethod.Get, false, false, false),
            "log query" => ("/api/logs/query", HttpMethod.Post, false, false, false),
            _ => throw new ArgumentException("unknown command", nameof(args)),
        };
        HashSet<string> allowed = ["--url", "--display-name"];
        string displayNameOption = "--display-name";
        if (options.TryGetValue(displayNameOption, out string? displayName))
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(displayName.Length, 120);
        }
        string managementPrefix = "/api/";
        if (path.StartsWith(managementPrefix, StringComparison.Ordinal))
        {
            allowed.UnionWith(["--cluster", "--peer"]);
            ArgumentException.ThrowIfNullOrWhiteSpace(options["--cluster"]);
            ArgumentException.ThrowIfNullOrWhiteSpace(options["--peer"]);
        }
        string[] identityCommands = ["configuration status", "trace status", "workflow get", "workflow run-status", "workflow cancel-status", "run-status", "workflow operation", "operation status", "snapshot capture-status", "snapshot restore-status", "file operation", "file recover", "update status", "request status", "safety status", "conflict status", "workflow detached-detail", "workflow detached-status"];
        if (identityCommands.Contains(name, StringComparer.Ordinal))
        {
            allowed.Add("--id");
        }
        string archivePrefix = "archive ";
        bool archive = name.StartsWith(archivePrefix, StringComparison.Ordinal);
        if (archive)
        {
            switch (name)
            {
                case "archive export": allowed.UnionWith(["--file", "--logs", "--operation-id"]); break;
                case "archive preview": allowed.UnionWith(["--file", "--mode", "--operation-id"]); break;
                case "archive import": allowed.UnionWith(["--id", "--confirmation"]); _ = OperationIdentifier(options); break;
                default: allowed.Add("--id"); break;
            }
        }
        string keyCluster = "cluster ";
        string fileRecover = "file recover";
        if (!archive && method != HttpMethod.Get && !string.Equals(name, fileRecover, StringComparison.Ordinal) && !secret && !name.StartsWith(keyCluster, StringComparison.Ordinal))
        {
            allowed.Add("--body");
        }
        string parse = "workflow parse";
        string validate = "workflow validate";
        string yamlOption = "--yaml";
        string bodyOption = "--body";
        if (string.Equals(name, parse, StringComparison.Ordinal) || string.Equals(name, validate, StringComparison.Ordinal))
        {
            allowed.Add("--yaml");
            if (options.ContainsKey(yamlOption) && options.ContainsKey(bodyOption))
            {
                throw new ArgumentException("select JSON body or YAML source", nameof(args));
            }
        }
        if (operation)
        {
            allowed.Add("--operation-id");
        }
        if (secret)
        {
            allowed.UnionWith(["--name", "--stdin", "--env"]);
        }
        if (options.Keys.Any(option => !allowed.Contains(option)))
        {
            throw new ArgumentException("unsupported option", nameof(args));
        }
        string address = options["--url"];
        Uri uri = new(address, UriKind.Absolute);
        if (!uri.IsLoopback)
        {
            throw new ArgumentException("local ClientHost loopback URL required", nameof(args));
        }
        string http = "http";
        string https = "https";
        string root = "/";
        if ((!string.Equals(uri.Scheme, http, StringComparison.Ordinal) && !string.Equals(uri.Scheme, https, StringComparison.Ordinal)) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !string.Equals(uri.AbsolutePath, root, StringComparison.Ordinal))
        {
            throw new ArgumentException("invalid management URL", nameof(args));
        }
        return new(uri, name, path, method, options, operation, secret, mutation);
    }

    /// <summary>パスへ組み込む明示IDを検証します。</summary>
    /// <returns>エスケープ済IDです。</returns>
    private static string Identifier(Dictionary<string, string> options)
    {
        string value = options["--id"];
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string dot = ".";
        string parent = "..";
        if (string.Equals(value, dot, StringComparison.Ordinal) || string.Equals(value, parent, StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("invalid identifier", nameof(options));
        }
        return Uri.EscapeDataString(value);
    }

    /// <summary>操作照会のGUIDを検証します。</summary>
    /// <returns>正規化した操作IDです。</returns>
    private static string OperationIdentifier(Dictionary<string, string> options)
    {
        Guid value = Guid.Parse(options["--id"]);
        if (value == Guid.Empty)
        {
            throw new ArgumentException("empty operation", nameof(options));
        }
        return value.ToString("D");
    }
}
