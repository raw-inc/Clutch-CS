using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>実行先ピアの候補だけを取得し、クライアント自身のNICと混ぜません。</summary>
internal sealed class SettingChoiceProvider(ManagementApi api, ConnectionState? connection = null, EditorDocument? document = null)
{
    private readonly Dictionary<(string Cluster, string Peer), ChoiceRead> cache = [];
    private string? context;
    private int version;

    /// <summary>接続実体と候補の更新時だけ、軽量化した設定欄へ再確認を通知します。</summary>
    internal int Version
    {
        get
        {
            ObserveContext();
            return version;
        }
    }

    /// <summary>LastSeenの毎回更新は無視し、再起動・再参加・実行先変更を区別します。</summary>
    private void ObserveContext()
    {
        string current = ContractJson.Hash(new
        {
            api.SelectedClusterId, api.SelectedPeerId, connection?.Connected, connection?.Current?.Coordinator, connection?.Current?.InstanceId, connection?.Current?.Status,
            Peers = connection?.Current?.Peers.OrderBy(peer => peer.PeerId, StringComparer.Ordinal)
                .Select(peer => new { peer.PeerId, peer.HostId, peer.InstanceId, peer.Status, peer.Priority, peer.ReadySince }),
        });
        if (!string.Equals(current, context, StringComparison.Ordinal))
        {
            context = current;
            cache.Clear();
            version++;
        }
        foreach (ChoiceRead item in cache.Values)
        {
            if (!item.Expired && item.Read is { IsCompleted: true } && item.ExpiresAt <= api.UtcNow)
            {
                item.Expired = true;
                version++;
            }
        }
    }

    /// <summary>転送元Hostの閲覧先を、所属クラスタの参加可能な一Peerへ限定します。</summary>
    /// <returns>PC選択と同じ優先度順のPeer、未確認ならnullです。</returns>
    internal string? ResolveHost(string host)
    {
        if (connection?.Connected != true || connection.Current is not { } current)
        {
            return null;
        }
        return current.Peers.Where(peer => string.Equals(peer.HostId, host, StringComparison.Ordinal)
            && ((current.Status == EPeerStatus.Detached && peer.InstanceId == current.InstanceId && peer.Status == EPeerStatus.Detached)
                || (current.Status != EPeerStatus.Detached && PeerStatusRules.CanParticipate(peer.Status))))
            .OrderByDescending(peer => peer.Priority).ThenBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).FirstOrDefault()?.PeerId;
    }

    /// <summary>PC別設定に必要な候補を取得し、動的選択ではクラスタの全観測PCを示します。</summary>
    /// <returns>保存済みのPC選択を含む、重複のないHost識別子です。</returns>
    internal IReadOnlyList<string> NetworkHosts(NodeDefinition node)
    {
        IReadOnlyList<PeerInfo> peers = connection?.Current?.Peers ?? [];
        string[] all = peers.Select(peer => peer.HostId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (node.Target is { } target)
        {
            if (target.SourcePeer || 0 < target.Groups?.Count)
            {
                return all;
            }
            return peers.Where(peer => target.Peers?.Contains(peer.PeerId, StringComparer.Ordinal) == true).Select(peer => peer.HostId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        WorkflowDefinition? workflow = document?.Draft.Document;
        WireDefinition[] wires = workflow?.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)
            && string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray() ?? [];
        if (wires.Length == 0)
        {
            return all;
        }
        HashSet<string> hosts = new(StringComparer.Ordinal);
        foreach (WireDefinition wire in wires)
        {
            NodeDefinition? selector = PcSelectionPorts.Origin(workflow!, wire);
            string modeKey = "mode", hostsKey = "hostIds", selected = "selected";
            if (selector is null || !string.Equals(selector.Type, PcSelectionPorts.SelectNode, StringComparison.Ordinal)
                || selector.Inputs?.Keys.Any(SettingPorts.IsSetting) == true
                || workflow!.Wires.Any(item => string.Equals(item.ToNode, selector.Id, StringComparison.Ordinal) && SettingPorts.IsSetting(item.ToPort))
                || !selector.Settings.TryGetProperty(modeKey, out JsonElement mode) || mode.ValueKind != JsonValueKind.String || !string.Equals(mode.GetString(), selected, StringComparison.Ordinal)
                || !selector.Settings.TryGetProperty(hostsKey, out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
            {
                return all;
            }
            hosts.UnionWith(ids.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
        }
        return hosts.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>明示された一つの実行先を選びます。</summary>
    /// <returns>複数対象・発生元依存はnullです。</returns>
    internal static string? TargetPeer(NodeDefinition node, string? coordinator)
    {
        if (node.Target is null)
        {
            return coordinator;
        }
        if (node.Target.SourcePeer || node.Target.Groups?.Count != 0 && node.Target.Groups is not null || node.Target.Peers?.Count != 1)
        {
            return null;
        }
        return node.Target.Peers[0];
    }

    /// <summary>描画間で短時間だけ同じ固定対象の照会を共有します。</summary>
    /// <returns>現在確認できた候補と取得不能の区別です。</returns>
    // 抑制理由: 照会Taskはcacheが保持し、各呼出元はWaitAsyncで自分の待機だけを取り消します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "照会Taskはcacheが所有し、呼出元へ取消可能な待機Taskを返します。")]
    internal Task<SettingNetworkChoices> GetAsync(NodeDefinition node, bool refresh = false, CancellationToken cancellationToken = default)
        => GetPeerAsync(ResolvePeer(node), refresh, cancellationToken);

    /// <summary>PC別設定の一行に対応するHostから、利用可能なPeerを選んで照会します。</summary>
    /// <returns>そのPCだけのNIC候補と取得不能の理由です。</returns>
    internal Task<SettingNetworkChoices> GetForHostAsync(string host, bool refresh = false, CancellationToken cancellationToken = default)
        => GetPeerAsync(ResolveHost(host), refresh, cancellationToken);

    /// <summary>同じPeerへの照会を設定欄間で共有します。</summary>
    /// <returns>検証できた候補または確認できない理由です。</returns>
    // 抑制理由: 照会Taskはcacheが所有し、各呼出元は自分の待機だけを取り消します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "cacheが照会Taskを所有し、呼出元には取消可能な待機を返します。")]
    private Task<SettingNetworkChoices> GetPeerAsync(string? peer, bool refresh, CancellationToken cancellationToken = default)
    {
        ObserveContext();
        string? cluster = api.SelectedClusterId;
        if (peer is null || cluster is null)
        {
            return Task.FromResult(new SettingNetworkChoices(null, [], "Select one execution peer to verify these choices."));
        }
        (string Cluster, string Peer) key = (cluster, peer);
        cache.TryGetValue(key, out ChoiceRead? cached);
        if (cached is not null && (!cached.Read.IsCompleted || !refresh && !cached.Expired))
        {
            return cached.Read.WaitAsync(cancellationToken);
        }
        if (32 <= cache.Count)
        {
            cache.Clear();
        }
        ChoiceRead entry = new() { Value = cached?.Value };
        cache[key] = entry;
        entry.Read = ReadCachedAsync(cluster, peer, entry, CancellationToken.None);
        return entry.Read.WaitAsync(cancellationToken);
    }

    /// <summary>一つの入力欄の終了で、他の入力欄と共有している照会を中断しません。</summary>
    /// <returns>現在の照会世代に対応する結果です。</returns>
    private async Task<SettingNetworkChoices> ReadCachedAsync(string cluster, string peer, ChoiceRead entry, CancellationToken cancellationToken = default)
    {
        SettingNetworkChoices result = await ReadAsync(cluster, peer, cancellationToken).ConfigureAwait(false);
        ObserveContext();
        if (!cache.TryGetValue((cluster, peer), out ChoiceRead? current) || !ReferenceEquals(current, entry))
        {
            return new(peer, [], "Network choices could not be verified. The saved value is retained.");
        }
        if (!string.Equals(ContractJson.Hash(entry.Value), ContractJson.Hash(result), StringComparison.Ordinal))
        {
            version++;
        }
        entry.Value = result;
        entry.ExpiresAt = api.UtcNow.AddSeconds(result.Problem is null ? 10
                                                                       : 2);
        return result;
    }

    /// <summary>設定を使う処理のPCを確認し、画面で選んだだけのPeerへ置き換えません。</summary>
    /// <returns>現在の対象を一台に特定できる場合のPeer。</returns>
    internal string? ResolvePeer(NodeDefinition node)
    {
        string[] valueTypes = ["value.interface", "value.file", "value.directory"];
        WorkflowDefinition? workflow = document?.Draft.Document;
        if (node.Target is null && valueTypes.Contains(node.Type, StringComparer.Ordinal) && workflow is not null)
        {
            string interfaceInput = "interfaceId";
            string interfaceSetting = SettingPorts.Name(interfaceInput);
            NodeDefinition[] consumers = workflow.Wires.Where(wire => string.Equals(wire.FromNode, node.Id, StringComparison.Ordinal)
                && (string.Equals(wire.ToPort, interfaceInput, StringComparison.Ordinal) || string.Equals(wire.ToPort, interfaceSetting, StringComparison.Ordinal)
                    || wire.ToPort is "$settings.path" or "$settings.destination"))
                .Select(wire => workflow.Nodes.FirstOrDefault(item => string.Equals(item.Id, wire.ToNode, StringComparison.Ordinal))).OfType<NodeDefinition>().ToArray();
            if (consumers.Length != 0)
            {
                string?[] targets = consumers.Select(ExecutionPeer).Distinct(StringComparer.Ordinal).ToArray();
                if (targets.Length == 1)
                {
                    return targets[0];
                }
                return null;
            }
        }
        return ExecutionPeer(node);
    }

    /// <summary>固定PC選択と現在のCoordinatorを使い、実行時の負荷選択は未確定として示します。</summary>
    /// <returns>設定候補を検査する一台。負荷依存・複数実行先はnull。</returns>
    private string? ExecutionPeer(NodeDefinition node)
    {
        if (node.Target is not null)
        {
            return TargetPeer(node, null);
        }
        if (connection?.Connected != true || connection.Current is not { } current)
        {
            return null;
        }
        WorkflowDefinition? workflow = document?.Draft.Document;
        WireDefinition[] wires = workflow?.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)
            && string.Equals(wire.ToPort, PcSelectionPorts.Execution, StringComparison.Ordinal)).ToArray() ?? [];
        if (wires.Length == 0)
        {
            if (current.Status == EPeerStatus.Detached)
            {
                return current.PeerId;
            }
            return current.Coordinator;
        }
        HashSet<string> hosts = new(StringComparer.Ordinal);
        foreach (WireDefinition wire in wires)
        {
            NodeDefinition? selector = PcSelectionPorts.Origin(workflow!, wire);
            if (selector is null || !string.Equals(selector.Type, PcSelectionPorts.SelectNode, StringComparison.Ordinal)
                || selector.Inputs?.Keys.Any(SettingPorts.IsSetting) == true
                || workflow!.Wires.Any(item => string.Equals(item.ToNode, selector.Id, StringComparison.Ordinal) && SettingPorts.IsSetting(item.ToPort)))
            {
                return null;
            }
            string modeKey = "mode";
            string hostKey = "hostIds";
            string all = "all";
            if (selector.Settings.TryGetProperty(modeKey, out JsonElement mode) && string.Equals(mode.GetString(), all, StringComparison.Ordinal))
            {
                hosts.UnionWith(current.Peers.Select(peer => peer.HostId));
            }
            else
            {
                if (!selector.Settings.TryGetProperty(hostKey, out JsonElement ids) || ids.ValueKind != JsonValueKind.Array
                    || ids.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    return null;
                }
                hosts.UnionWith(ids.EnumerateArray().Select(item => item.GetString()!));
            }
        }
        if (node.PcExecution == EPcExecutionMode.All && hosts.Count != 1)
        {
            return null;
        }
        return current.Peers.Where(peer => hosts.Contains(peer.HostId) && (PeerStatusRules.CanParticipate(peer.Status)
                || current.Status == EPeerStatus.Detached && peer.InstanceId == current.InstanceId))
            .OrderByDescending(peer => peer.Priority).ThenBy(peer => peer.ReadySince).ThenBy(peer => peer.PeerId, StringComparer.Ordinal).FirstOrDefault()?.PeerId;
    }

    /// <summary>設定名が既知のNIC選択に該当するか確認します。</summary>
    /// <returns>取得対象の設定かどうかです。</returns>
    internal static bool Supports(string key) => key is "interfaceId" or "localAddress" or "bindAddress";

    /// <summary>候補の再取得で、通信・Host選択と無関係な入力欄まで作り直しません。</summary>
    /// <returns>接続先の観測結果を表示に使用する設定があるかどうかです。</returns>
    internal static bool UsesChoices(NodeDescriptor descriptor)
    {
        string propertiesKey = "properties";
        return descriptor.SettingsSchema.ValueKind == JsonValueKind.Object && descriptor.SettingsSchema.TryGetProperty(propertiesKey, out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Any(property => Supports(property.Name) || property.Name is "sourceHost" or "hostIds" or "peerId");
    }

    /// <summary>型付き候補だけを保ち、任意の応答本文を警告へそのまま出力しません。</summary>
    /// <returns>実行先から得たNICです。</returns>
    private async Task<SettingNetworkChoices> ReadAsync(string cluster, string peer, CancellationToken cancellationToken = default)
    {
        try
        {
            JsonElement response = await api.GetForPeerAsync<JsonElement>("/api/network", peer, cluster, cancellationToken).ConfigureAwait(false);
            return Parse(peer, response);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return new(peer, [], "Network choices could not be verified. The saved value is retained.");
        }
    }

    /// <summary>重複や欠損に耐える有限の応答読み取りです。</summary>
    /// <returns>最大256NICの検証済み候補です。</returns>
    internal static SettingNetworkChoices Parse(string peer, JsonElement response)
    {
        List<SettingNetworkInterface> items = [];
        string idKey = "id";
        string nameKey = "name";
        string addressesKey = "addresses";
        if (response.ValueKind != JsonValueKind.Array)
        {
            return new(peer, [], "Network choices could not be verified. The saved value is retained.");
        }
        foreach (JsonElement item in response.EnumerateArray().Take(256))
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(idKey, out JsonElement id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string identifier = id.GetString()!;
            string name = identifier;
            if (item.TryGetProperty(nameKey, out JsonElement label) && label.ValueKind == JsonValueKind.String)
            {
                name = label.GetString()!;
            }
            string[] addresses = [];
            if (item.TryGetProperty(addressesKey, out JsonElement values) && values.ValueKind == JsonValueKind.Array)
            {
                addresses = values.EnumerateArray().Take(64).Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            }
            if (!items.Any(existing => string.Equals(existing.Id, identifier, StringComparison.Ordinal)))
            {
                string statusKey = "status";
                string? status = null;
                if (item.TryGetProperty(statusKey, out JsonElement state) && state.ValueKind == JsonValueKind.String)
                {
                    status = state.GetString();
                }
                items.Add(new(identifier, name, addresses, status));
            }
        }
        return new(peer, items, null);
    }

    /// <summary>現在の一回の照会と、一度だけ通知する更新期限を保持します。</summary>
    private sealed record ChoiceRead
    {
        internal Task<SettingNetworkChoices> Read
        {
            get; set;
        } = null!;

        internal SettingNetworkChoices? Value
        {
            get; set;
        }

        internal DateTimeOffset ExpiresAt
        {
            get; set;
        }

        internal bool Expired
        {
            get; set;
        }
    }
}
