using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Resources;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using Tomlyn;
using Tomlyn.Model;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

/// <summary>標準TOMLを使って起動設定を読み、場所と型を検証します。</summary>
public static class CommonConfigurationReader
{
    /// <summary>設定ファイルの場所を基準にパスを解決します。</summary>
    /// <returns>検証済みの共通設定です。</returns>
    public static CommonConfiguration Read(string text, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        TomlTable document = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new FormatException("TOML設定がありません。");
        Known(document, "cluster", "storage", "time", "files", "processes", "resources", "media", "logs", "groups", "workflows", "updates", "safety", "detached", "transferQueue");
        TomlTable transferQueue = Table(document, "transferQueue");
        Known(transferQueue, "enabled", "networksFile");
        TomlTable cluster = Table(document, "cluster", true);
        TomlTable storage = Table(document, "storage");
        TomlTable time = Table(document, "time");
        TomlTable files = Table(document, "files");
        TomlTable processes = Table(document, "processes");
        TomlTable resources = Table(document, "resources");
        TomlTable media = Table(document, "media");
        TomlTable logs = Table(document, "logs");
        TomlTable updates = Table(document, "updates");
        UpdatesOptions defaultUpdates = new();
        TomlTable safety = Table(document, "safety");
        TomlTable detached = Table(document, "detached");
        Known(detached, "edit", "compute", "fileChange", "processChange", "externalCommunication");
        Known(safety, "confirmation");
        Known(cluster, "id", "peerId", "hostId", "address", "broadcast", "port", "priority", "initialize", "interfaceId", "syncSessionLifetimeSeconds", "maximumSyncSnapshotBytes", "maximumSyncSessions");
        Known(storage, "directory", "backend", "purposes", "availability", "history");
        Known(time, "zone");
        Known(files, "roots", "conflictPolicy", "internalFailurePolicy", "recoveryDirectory");
        Known(processes, "defaultRecoveryAttempts", "defaultReadyTimeoutMilliseconds", "recoveryDelayMilliseconds", "pollMilliseconds");
        Known(resources, "sampleIntervalMilliseconds", "historyCapacity", "historyRetentionHours", "nvidiaSmiPath");
        Known(media, "ffmpegPath", "ffprobePath");
        Known(logs, "directory", "queueBytes", "interfaceId", "listenPort", "rollingTime", "retention");
        Known(updates, "installationRoot", "stagingRoot", "backupRoot", "updaterExecutablePath", "serviceKind", "serviceName", "stopTimeoutMilliseconds", "startTimeoutMilliseconds", "forceStopOnTimeout", "rollbackFailurePolicy", "interfaceId");
        string address = Text(cluster, "address");
        string broadcast = Text(cluster, "broadcast");
        if (!IPAddress.TryParse(address, out IPAddress? localIp) || localIp.AddressFamily != AddressFamily.InterNetwork || localIp.Equals(IPAddress.Any))
        {
            throw new FormatException("cluster.addressは待受する具体的なIPv4で指定してください。");
        }
        if (!IPAddress.TryParse(broadcast, out IPAddress? broadcastIp) || broadcastIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FormatException("cluster.broadcastは明示したネットワークのIPv4で指定してください。");
        }
        int port = Integer(cluster, "port", 47740);
        if (port < 1 || 65535 < port)
        {
            throw new FormatException("cluster.portが範囲外です。");
        }
        string directory = Resolve(baseDirectory, Text(storage, "directory", "data"));
        string backend = Text(storage, "backend", "sqlite");
        string jsonBackend = "json";
        string sqliteBackend = "sqlite";
        if (!string.Equals(backend, jsonBackend, StringComparison.Ordinal) && !string.Equals(backend, sqliteBackend, StringComparison.Ordinal))
        {
            throw new FormatException("storage.backendはsqliteまたはjsonを指定してください。");
        }
        ClusterOptions options = new(Text(cluster, "id"), Text(cluster, "peerId"),
            Text(cluster, "hostId", Dns.GetHostName()), address, broadcast, port,
            Integer(cluster, "priority", 0), Boolean(cluster, "initialize"), directory);
        options = SynchronizationOptionsReader.Apply(options, cluster);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Text(time, "zone", "UTC"));
        Dictionary<string, IReadOnlyList<string>> groups = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> group in Table(document, "groups"))
        {
            groups.Add(group.Key, Strings(group.Value));
        }
        FileSystemOptions fileOptions = new(Array(files, "roots").Select(path => Resolve(baseDirectory, path)).ToArray(),
            Choice<EFileConflictPolicy>(files, "conflictPolicy", EFileConflictPolicy.Fail),
            Choice<EFileInternalFailurePolicy>(files, "internalFailurePolicy", EFileInternalFailurePolicy.Keep),
            Resolve(baseDirectory, Text(files, "recoveryDirectory", Path.Combine(directory, "file-recovery"))), UseOperatingSystemAccess: true);
        ProcessOptions processOptions = new(Limit(processes, "defaultRecoveryAttempts", 3), Limit(processes, "defaultReadyTimeoutMilliseconds", 30000),
            Positive(processes, "recoveryDelayMilliseconds", 1000), Positive(processes, "pollMilliseconds", 250));
        ResourceOptions resourceOptions = new(Positive(resources, "sampleIntervalMilliseconds", 3000), Positive(resources, "historyCapacity", 720),
            Positive(resources, "historyRetentionHours", 24), OptionalPath(resources, "nvidiaSmiPath", baseDirectory));
        return new CommonConfiguration(options with { InterfaceId = OptionalText(cluster, "interfaceId") }, directory, backend, zone,
            Array(document, "workflows").Select(path => Resolve(baseDirectory, path)).ToArray(),
            fileOptions, groups)
        {
            TransferQueueEnabled = Boolean(transferQueue, "enabled", true),
            TransferQueueNetworksFile = OptionalPath(transferQueue, "networksFile", baseDirectory),
            Persistence = ReadPersistence(storage, backend),
            HistoryArchive = HistoryArchiveOptionsReader.Read(Table(storage, "history")),
            Availability = AvailabilityOptionsReader.Read(Table(storage, "availability")),
            Processes = processOptions,
            Detached = new DetachedOperationOptions(Boolean(detached, "edit", true), Boolean(detached, "compute", true),
                Boolean(detached, "fileChange", true), Boolean(detached, "processChange", true), Boolean(detached, "externalCommunication", true)),
            SafetyConfirmation = Choice<ESafetyConfirmation>(safety, "confirmation", ESafetyConfirmation.None),
            Resources = resourceOptions,
            Updates = new UpdatesOptions(OptionalPath(updates, "installationRoot", baseDirectory), OptionalPath(updates, "stagingRoot", baseDirectory),
                OptionalPath(updates, "backupRoot", baseDirectory), OptionalPath(updates, "updaterExecutablePath", baseDirectory),
                Choice<EUpdateServiceKind>(updates, "serviceKind", EUpdateServiceKind.DirectProcess),
                Text(updates, "serviceName", "ReArtWord.Contents.BackgroundService"), Positive(updates, "stopTimeoutMilliseconds", defaultUpdates.StopTimeoutMilliseconds),
                Positive(updates, "startTimeoutMilliseconds", defaultUpdates.StartTimeoutMilliseconds), Boolean(updates, "forceStopOnTimeout"),
                Choice<EUpdateRollbackFailurePolicy>(updates, "rollbackFailurePolicy", EUpdateRollbackFailurePolicy.ExcludeFailed), OptionalText(updates, "interfaceId") ?? OptionalText(cluster, "interfaceId"), address, options.Id, broadcast, port),
            Logs = new LogOptions(options.PeerId, Resolve(baseDirectory, Text(logs, "directory", Path.Combine(directory, "logs"))),
                Retention: ReadRetention(Table(logs, "retention")),
                QueueBytes: Positive(logs, "queueBytes", 16777216),
                InterfaceId: OptionalText(logs, "interfaceId"), LocalAddress: address, ListenPort: Integer(logs, "listenPort", 0),
                RollingTime: Text(logs, "rollingTime", "00:00"), TimeZoneId: zone.Id),
            FfmpegPath = OptionalPath(media, "ffmpegPath", baseDirectory) ?? "ffmpeg",
            FfprobePath = OptionalPath(media, "ffprobePath", baseDirectory) ?? "ffprobe",
        };
    }

    /// <summary>省略時の共通方式と、原子用途群ごとの明示方式を読み分けます。</summary>
    /// <returns>用途を別の方式へ暗黙移行しない起動方針です。</returns>
    private static PersistenceOptions ReadPersistence(TomlTable storage, string backend)
    {
        TomlTable purposes = Table(storage, "purposes");
        Known(purposes, "configuration", "runtime", "processes", "diagnostics");
        EStorageBackend fallback = Enum.Parse<EStorageBackend>(backend, true);
        Dictionary<EStoragePurpose, EStorageBackend> selections = [];
        foreach (string key in purposes.Keys)
        {
            selections.Add(Enum.Parse<EStoragePurpose>(key, true), Choice(purposes, key, fallback));
        }
        return new(fallback, selections);
    }

    /// <summary>子テーブルを読み取ります。</summary>
    /// <returns>存在するテーブルまたは省略時の空テーブルです。</returns>
    internal static TomlTable Table(TomlTable parent, string name, bool required = false)
    {
        if (parent.TryGetValue(name, out object? value))
        {
            return value as TomlTable ?? throw new FormatException("設定セクションはTOMLテーブルで指定してください。");
        }
        if (required)
        {
            throw new FormatException("必須の設定セクションがありません。");
        }
        return new TomlTable();
    }

    /// <summary>明示された文字列と省略時の既定値を読みます。</summary>
    /// <returns>文字列設定です。</returns>
    private static string Text(TomlTable table, string name, string? fallback = null)
    {
        if (table.TryGetValue(name, out object? value))
        {
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            throw new FormatException("文字列設定の型が一致しないか空です。");
        }
        return fallback ?? throw new FormatException("必須の文字列設定がありません。");
    }

    /// <summary>整数設定の型を検証して読みます。</summary>
    /// <returns>指定した整数です。</returns>
    internal static int Integer(TomlTable table, string name, int fallback)
    {
        if (table.TryGetValue(name, out object? value))
        {
            return value switch
            {
                long number => checked((int)number),
                int number => number,
                _ => throw new FormatException("整数設定の型が一致しません。"),
            };
        }
        return fallback;
    }

    /// <summary>真偽値設定を読みます。</summary>
    /// <returns>指定値または未指定時の既定値です。</returns>
    private static bool Boolean(TomlTable table, string name, bool fallback = false)
    {
        if (table.TryGetValue(name, out object? value))
        {
            if (value is bool flag)
            {
                return flag;
            }
            throw new FormatException("真偽値設定の型が一致しません。");
        }
        return fallback;
    }

    /// <summary>省略可能な文字列配列を読みます。</summary>
    /// <returns>設定された一覧です。</returns>
    private static string[] Array(TomlTable table, string name)
    {
        if (table.TryGetValue(name, out object? value))
        {
            return Strings(value);
        }
        return [];
    }

    /// <summary>TOML配列内の文字列型を検証します。</summary>
    /// <returns>文字列の一覧です。</returns>
    private static string[] Strings(object value)
    {
        if (value is not TomlArray array)
        {
            throw new FormatException("一覧は文字列配列で指定してください。");
        }
        return array.Select(static item => item as string ?? throw new FormatException("一覧の要素は文字列で指定してください。")).ToArray();
    }

    /// <summary>設定ファイル相対のパスを絶対パスへ変換します。</summary>
    /// <returns>OSのパス規則で解決した場所です。</returns>
    private static string Resolve(string directory, string path)
    {
        return Path.GetFullPath(path, directory);
    }

    /// <summary>正数の実行間隔と履歴容量を検証します。</summary>
    /// <returns>指定値または既定値です。</returns>
    private static int Positive(TomlTable table, string name, int fallback)
    {
        int value = Integer(table, name, fallback);
        if (value < 1)
        {
            throw new FormatException("間隔と容量は正数で指定してください。");
        }
        return value;
    }

    /// <summary>有限既定と明示した無制限を区別します。</summary>
    /// <returns>有限値、またはunlimited指定のnullです。</returns>
    private static int? Limit(TomlTable table, string name, int fallback)
    {
        string unlimited = "unlimited";
        if (table.TryGetValue(name, out object? value) && value is string text && string.Equals(text, unlimited, StringComparison.Ordinal))
        {
            return null;
        }
        return Positive(table, name, fallback);
    }

    /// <summary>宣言された列挙値だけを設定として読みます。</summary>
    /// <returns>指定値または既定値です。</returns>
    internal static T Choice<T>(TomlTable table, string name, T fallback) where T : struct, Enum
    {
        if (!table.ContainsKey(name))
        {
            return fallback;
        }
        if (Enum.TryParse(Text(table, name), true, out T parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        throw new FormatException("設定の選択肢が不正です。");
    }

    /// <summary>明示実行物は設定ファイル相対で解決します。</summary>
    /// <returns>指定場所、または未指定のnullです。</returns>
    private static string? OptionalPath(TomlTable table, string name, string directory)
    {
        if (table.ContainsKey(name))
        {
            return Resolve(directory, Text(table, name));
        }
        return null;
    }

    /// <summary>省略できる文字列設定です。</summary>
    /// <returns>型を確認した指定値、またはnullです。</returns>
    private static string? OptionalText(TomlTable table, string name)
    {
        if (table.ContainsKey(name))
        {
            return Text(table, name);
        }
        return null;
    }

    /// <summary>設定の誤記を既定値への暗黙変換として扱いません。</summary>
    internal static void Known(TomlTable table, params string[] names)
    {
        if (table.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
        {
            throw new FormatException("未定義の設定項目があります。項目名を確認してください。");
        }
    }

    /// <summary>レベルごとの平文保持と全体保持を読みます。</summary>
    /// <returns>明示した保持方針です。</returns>
    private static List<LogRetentionRule>? ReadRetention(TomlTable table)
    {
        if (table.Count == 0)
        {
            return null;
        }
        List<LogRetentionRule> result = [];
        foreach (string level in table.Keys)
        {
            TomlTable policy = Table(table, level, true);
            Known(policy, "rawDays", "totalDays");
            result.Add(new LogRetentionRule(level, Integer(policy, "rawDays", 7), Limit(policy, "totalDays", 30)));
        }
        return result;
    }
}
