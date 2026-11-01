using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>一時設定だけを使用して実CoreServiceプロセスを起動します。</summary>
internal sealed class CorePeer : IAsyncDisposable
{
    private readonly UdpObserver observer;
    private readonly string clusterId;
    private readonly string peerId;
    private readonly string configurationPath;
    private readonly string executable;
    private readonly bool redirectErrors;
    private Process? process;
    private Task<string> errors = Task.FromResult(string.Empty);

    /// <summary>別々の保存先と管理待受を持つPeerを構成します。</summary>
    internal CorePeer(string clusterId, string peerId, int clusterPort, bool initialize, string? hostId = null, int priority = 0, string? executablePath = null, bool redirectErrors = true)
    {
        this.redirectErrors = redirectErrors;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string suffix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            suffix = ".exe";
        }
        executable = executablePath ?? Environment.GetEnvironmentVariable("CONTENTS_TEST_CORE")
            ?? Path.Combine(root, "src", "ReArtWord.Contents.BackgroundService.CoreService", "bin", configuration, "net10.0", "Clutch-CS.CoreService" + suffix);
        DirectoryPath = Path.Combine(root, ".work", "integration", clusterId, peerId);
        Directory.CreateDirectory(DirectoryPath);
        configurationPath = Path.Combine(DirectoryPath, "contents.toml");
        this.clusterId = clusterId;
        this.peerId = peerId;
        InterfaceId = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        File.WriteAllText(configurationPath, $$"""
            [cluster]
            id = "{{clusterId}}"
            peerId = "{{peerId}}"
            hostId = {{JsonSerializer.Serialize(hostId ?? "host-local")}}
            priority = {{priority}}
            address = "127.0.0.1"
            broadcast = "127.255.255.255"
            port = {{clusterPort}}
            initialize = {{JsonSerializer.Serialize(initialize)}}
            [storage]
            directory = "{{DirectoryPath.Replace(Path.DirectorySeparatorChar, '/')}}/state"
            [files]
            roots = ["{{DirectoryPath.Replace(Path.DirectorySeparatorChar, '/')}}/content"]
            [logs]
            interfaceId = '{{InterfaceId}}'
            listenPort = 0
            """);

        // 設定保存の失敗時に、破棄できない待受を残さないよう最後に確保します。
        observer = new(new ObserverOptions(Guid.NewGuid().ToString("N"), new(InterfaceId, "127.0.0.1", "127.255.255.255", clusterPort)));
    }

    /// <summary>失敗時の状態とログを調査する一時保存先です。</summary>
    internal string DirectoryPath
    {
        get;
    }

    /// <summary>実在するループバック NICの識別子です。</summary>
    internal string InterfaceId
    {
        get;
    }

    /// <summary>所有する試験Coreだけへ渡す環境変数です。</summary>
    internal Dictionary<string, string> StartEnvironment
    {
        get;
    } = [];

    /// <summary>管理APIが実際に応答するまで待ちます。</summary>
    /// <returns>Hostの起動確認です。</returns>
    // 抑制理由: 起動した子プロセスの標準error読み出しをフィールドで所有し、停止時に必ずawaitして保存します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "子processのstderrは起動確認またはStopAsyncでawaitし、所有processと同じ寿命で管理します。")]
    [SuppressMessage("SourceQuality", "RAW330", Justification = "期限切れの診断を保存した後、元の中断理由とスタックを失わず試験へ返します。")]
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Guid? previousInstance = FindLatestPeer()?.InstanceId;
        await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = redirectErrors };
        foreach (KeyValuePair<string, string> item in StartEnvironment)
        {
            start.Environment[item.Key] = item.Value;
        }
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configurationPath);
        process = Process.Start(start) ?? throw new IOException("試験用CoreServiceを起動できません。");
        if (redirectErrors)
        {
            errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }
        JsonElement? lastHealth = null;
        string? lastProblem = null;
        try
        {
            await WaitAsync(async token =>
            {
                if (process.HasExited)
                {
                    throw new IOException("試験用CoreServiceが起動中に終了しました。" + await errors.ConfigureAwait(false));
                }
                PeerInfo? observed = FindLatestPeer();
                if (observed is null || observed.InstanceId == previousInstance)
                {
                    return false;
                }
                try
                {
                    JsonElement health = await GetAsync<JsonElement>("health", token).ConfigureAwait(false);
                    lastHealth = health;
                    return health.GetProperty("processId").GetInt32() == process.Id;
                }
                catch (InvalidOperationException exception)
                {
                    lastProblem = exception.Message;
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "startup-failure.json"), JsonSerializer.Serialize(new
            {
                expectedProcessId = process.Id, process.HasExited, previousInstance, lastHealth, lastProblem,
                observed = observer.GetClusters(),
            }, ContractJson.Options), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>所有している実プロセスだけを終了して障害を再現します。</summary>
    /// <returns>実終了の確認です。</returns>
    internal Task StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync(true, cancellationToken);

    /// <summary>管理していたアプリケーションを残し、Core単体の停止を再現します。</summary>
    /// <returns>Coreだけの終了確認です。</returns>
    internal Task StopHostOnlyAsync(CancellationToken cancellationToken = default) => StopCoreAsync(false, cancellationToken);

    /// <summary>試験が指定したプロセス境界だけを終了します。</summary>
    /// <returns>所有した標準出力と実プロセスの回収です。</returns>
    private async Task StopCoreAsync(bool includeChildren, CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill(includeChildren);
            }
            await ProcessCompletion.WaitAsync(process, cancellationToken).ConfigureAwait(false);
            try
            {
                string error = await errors.ConfigureAwait(false);
                if (error.Length != 0)
                {
                    await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "startup-error.txt"), error, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // 診断を保存できなくても、停止確認済みのプロセスハンドルは解放します。
                process.Dispose();
                process = null;
            }
        }
    }

    /// <summary>契約の型を保持したJSONを実UDPで照会します。</summary>
    /// <returns>実管理APIの応答です。</returns>
    internal Task<T> GetAsync<T>(string operation, CancellationToken cancellationToken = default) => SendAsync<T>(operation, null, true, false, cancellationToken);

    /// <summary>対象と検索条件を持つ参照専用操作を実UDPで送ります。</summary>
    /// <returns>実管理操作の応答です。</returns>
    internal Task<T> GetAsync<T>(string operation, object? body, CancellationToken cancellationToken = default) => SendAsync<T>(operation, body, true, false, cancellationToken);

    /// <summary>同じ本文とドメイン操作IDを実UDPへ送ります。</summary>
    /// <returns>実管理APIの応答です。</returns>
    internal Task<T> PostAsync<T>(string operation, object body, CancellationToken cancellationToken = default) => SendAsync<T>(operation, body, false, false, cancellationToken);

    /// <summary>永続操作履歴を作らない編集位置通知を実UDPで送ります。</summary>
    /// <returns>通知の受付応答です。</returns>
    internal Task<T> NotifyAsync<T>(string operation, object body, CancellationToken cancellationToken = default) => SendAsync<T>(operation, body, false, true, cancellationToken);

    /// <summary>発見した当該Peerへ参照または永続管理操作を送ります。</summary>
    /// <returns>実際に応答した管理結果です。</returns>
    private async Task<T> SendAsync<T>(string operation, object? body, bool readOnly, bool transient, CancellationToken cancellationToken = default)
    {
        PeerInfo target = FindLatestPeer() ?? throw new InvalidOperationException("試験Peerを未発見です。");
        NodeValue? payload = null;
        if (body is not null)
        {
            payload = NodeValue.FromJson(JsonSerializer.SerializeToElement(body, ContractJson.Options));
        }
        ManagementCommand command = new(Guid.NewGuid(), clusterId, peerId, target.InstanceId, DateTimeOffset.UtcNow.AddMinutes(1), operation, payload);
        ManagementReply response;
        if (readOnly)
        {
            response = await observer.InspectWithRetryAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (transient)
            {
                response = await observer.NotifyAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (response.Outcome == EOutcome.Unknown)
                {
                    response = await ConfirmAsync(command, response, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        if (response.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidOperationException($"管理応答: {response.Reason}; operation={operation}; peer={peerId}; request={command.RequestId}; evidence={DirectoryPath}");
        }
        return response.Value!.Json.Deserialize<T>(ContractJson.Options) ?? throw new InvalidDataException("API応答がありません。");
    }

    /// <summary>応答を確認できない変更操作は再送せず、同じ元要求の処理結果を照会します。</summary>
    /// <returns>期限内に確認した結果、または元の結果不明です。</returns>
    private async Task<ManagementReply> ConfirmAsync(ManagementCommand command, ManagementReply unknown, CancellationToken cancellationToken = default)
    {
        string unconfirmed = "Observer.ReceiptUnconfirmed";
        string missing = "Execution.ReceiptMissing";
        while (DateTimeOffset.UtcNow < command.ExpiresAt)
        {
            ManagementQueryResult queried = await observer.QueryAsync(command, cancellationToken).ConfigureAwait(false);
            if (queried.Reply is not null)
            {
                return queried.Reply;
            }
            if (!queried.Running && !string.Equals(queried.Reason, unconfirmed, StringComparison.Ordinal)
                && !string.Equals(queried.Reason, missing, StringComparison.Ordinal))
            {
                break;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return unknown;
    }

    /// <summary>複数回の再起動で古い通知が残っていても、直近に受信した実体を選びます。</summary>
    /// <returns>試験対象の最新の参加情報です。</returns>
    private PeerInfo? FindLatestPeer() => observer.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, clusterId, StringComparison.Ordinal))
        .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)).MaxBy(peer => peer.LastSeen);

    /// <summary>固定の待ち時間ではなく、実条件を確認します。</summary>
    /// <returns>有限上限内での条件成立です。</returns>
    internal static Task WaitAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken cancellationToken = default)
        => WaitAsync(condition, TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>再試行間隔と実転送を含め、用途に対応した有限上限で条件を確認します。</summary>
    /// <returns>指定上限内での条件成立です。</returns>
    internal static async Task WaitAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan maximumWait, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumWait);
        while (!await condition(timeout.Token).ConfigureAwait(false))
        {
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>同じ試験で使うUDPポートを、選択が終わるまで保持して重複なく取得します。</summary>
    /// <param name="count">必要なポート数です。</param>
    /// <param name="excluded">まだ待受を開始していない、選択済みのポートです。</param>
    /// <returns>予約を解放したポート番号です。</returns>
    internal static int[] AvailableUdpPorts(int count, params int[] excluded)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        List<Socket> held = [];
        List<int> ports = new(count);
        try
        {
            while (ports.Count < count)
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                held.Add(socket);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                if (!excluded.Contains(port))
                {
                    ports.Add(port);
                }
            }
            return ports.ToArray();
        }
        finally
        {
            foreach (Socket socket in held)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>試験用の構成ごとに使用可能なローカルポートを選択します。</summary>
    /// <returns>短時間だけ予約確認したポートです。</returns>
    internal static int AvailablePort(bool tcp = false)
    {
        SocketType type = SocketType.Dgram;
        ProtocolType protocol = ProtocolType.Udp;
        if (tcp)
        {
            type = SocketType.Stream;
            protocol = ProtocolType.Tcp;
        }
        using Socket socket = new(AddressFamily.InterNetwork, type, protocol);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>所有Hostの実停止後にHTTP資源を解放します。</summary>
    /// <returns>試験用の構成の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
        }
    }
}
