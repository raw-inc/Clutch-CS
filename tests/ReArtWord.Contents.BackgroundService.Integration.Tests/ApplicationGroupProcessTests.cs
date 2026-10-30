using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>ClientHost・Core・監視アプリの実プロセスを通してグループを操作します。</summary>
public sealed class ApplicationGroupProcessTests
{
    /// <summary>選択先だけのグループを起動し、一人の終了から全員を順番に復旧します。</summary>
    /// <returns>実HTTP、管理通信、常駐監視、操作識別子の照合です。</returns>
    [Fact]
    public async Task ClientHostGroupRecoveryIsOrderedAndPeerLocalAsync()
    {
        using ApplicationProcessFixture server = new(delayMilliseconds: -1), player = new(delayMilliseconds: -1);
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        ClientHostProcess client = new(Path.Combine(first.DirectoryPath, "client"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable clientLifetime = client.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await ApplicationLifecycleProcessTests.ReadyAsync(first, timeout.Token);
        await ApplicationLifecycleProcessTests.ReadyAsync(second, timeout.Token);
        await client.StartAsync(timeout.Token);
        using (HttpResponseMessage binding = await client.SendAsync(HttpMethod.Post, "client/network",
            new ClientNetworkBinding(first.InterfaceId, "127.0.0.1", "127.255.255.255", port), cancellationToken: timeout.Token))
        {
            binding.EnsureSuccessStatusCode();
        }
        await CorePeer.WaitAsync(async token =>
        {
            using HttpResponseMessage response = await client.SendAsync(HttpMethod.Get, "client/clusters", cancellationToken: token).ConfigureAwait(false);
            DiscoveredCluster[]? clusters = await response.Content.ReadFromJsonAsync<DiscoveredCluster[]>(ContractJson.Options, token).ConfigureAwait(false);
            return clusters?.Any(item => string.Equals(item.ClusterId, cluster, StringComparison.Ordinal) && item.Peers.Count == 2) == true;
        }, timeout.Token);
        foreach (CorePeer peer in new[] { first, second })
        {
            Assert.Equal("succeeded", (await peer.PostAsync<JsonElement>("processes.register", new ProcessRegistrationRequest(server.Definition with { Id = "server" }), timeout.Token)).GetProperty("outcome").GetString());
            Assert.Equal("succeeded", (await peer.PostAsync<JsonElement>("processes.register", new ProcessRegistrationRequest(player.Definition with { Id = "player" }), timeout.Token)).GetProperty("outcome").GetString());
        }
        ProcessGroupDefinition definition = new("display", "Display system", [new("server"), new("player", 300)],
            RecoveryMode: EProcessGroupRecoveryMode.EntireGroup, RecoveryDelayMilliseconds: 300, StopTimeoutMilliseconds: 1);
        Assert.Equal("succeeded", (await PostAsync<JsonElement>(client, cluster, "register", new ProcessGroupRegistrationRequest(definition), timeout.Token)).GetProperty("outcome").GetString());
        ProcessGroupOperationResult accepted = await PostAsync<ProcessGroupOperationResult>(client, cluster, "command", new ProcessGroupCommandRequest("display", EProcessGroupCommand.Start), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, accepted.Outcome);
        await CompletedAsync(first, accepted.Status!.OperationId!.Value, timeout.Token);
        await CorePeer.WaitAsync(_ => Task.FromResult(server.Starts().Length == 1 && player.Starts().Length == 1), timeout.Token);
        Assert.True(TimeSpan.FromMilliseconds(300) <= Started(player, 0) - Started(server, 0));
        Assert.Empty(await second.GetAsync<RegisteredProcessGroup[]>("processes.groups.list", timeout.Token));
        JsonElement[] untouched = await second.GetAsync<JsonElement[]>("processes.list", timeout.Token);
        Assert.All(untouched, application => Assert.False(application.GetProperty("observed").GetProperty("status").GetProperty("running").GetBoolean()));
        int originalServer = int.Parse(server.Starts()[0][0], CultureInfo.InvariantCulture);
        using (Process failed = Process.GetProcessById(originalServer))
        {
            failed.Kill();
            await failed.WaitForExitAsync(timeout.Token);
        }
        await CorePeer.WaitAsync(async token =>
        {
            RegisteredProcessGroup group = Assert.Single(await first.GetAsync<RegisteredProcessGroup[]>("processes.groups.list", token).ConfigureAwait(false));
            return group.Status.OperationId != accepted.Status.OperationId && group.Status.Outcome == EOutcome.Succeeded;
        }, timeout.Token);
        await CorePeer.WaitAsync(_ => Task.FromResult(server.Starts().Length == 2 && player.Starts().Length == 2), timeout.Token);
        Assert.True(TimeSpan.FromMilliseconds(300) <= Started(player, 1) - Started(server, 1));
        ProcessGroupOperationResult stopped = await PostAsync<ProcessGroupOperationResult>(client, cluster, "command", new ProcessGroupCommandRequest("display", EProcessGroupCommand.Stop), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, stopped.Outcome);
        await CompletedAsync(first, stopped.Status!.OperationId!.Value, timeout.Token);
        RegisteredProcessGroup current = Assert.Single(await first.GetAsync<RegisteredProcessGroup[]>("processes.groups.list", timeout.Token));
        Assert.True(current.Status.RecoverySuppressed);
        Assert.Equal("succeeded", (await PostAsync<JsonElement>(client, cluster, "remove", new ProcessGroupRemovalRequest("display", current.Version), timeout.Token)).GetProperty("outcome").GetString());
    }

    /// <summary>起動済みアプリ自身の実時刻を昇順で読みます。</summary>
    /// <returns>指定回のOS起動時刻です。</returns>
    private static DateTimeOffset Started(ApplicationProcessFixture app, int index)
        => app.Starts().Select(entry => DateTimeOffset.Parse(entry[1], CultureInfo.InvariantCulture)).Order().ElementAt(index);

    /// <summary>受付と完了を同じ操作IDで照合します。</summary>
    /// <returns>実Coreの所有処理が完了した時点です。</returns>
    private static Task CompletedAsync(CorePeer peer, Guid operation, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token =>
        {
            RegisteredProcessGroup group = Assert.Single(await peer.GetAsync<RegisteredProcessGroup[]>("processes.groups.list", token).ConfigureAwait(false));
            Assert.NotEqual(EProcessGroupPhase.Paused, group.Status.Phase);
            return group.Status.OperationId == operation && group.Status.Outcome == EOutcome.Succeeded;
        }, cancellationToken);

    /// <summary>ブラウザーと同じ入口で所有先と要求IDを指定します。</summary>
    /// <returns>Coreが返した操作結果です。</returns>
    private static async Task<T> PostAsync<T>(ClientHostProcess client, string cluster, string operation, object body, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.SendAsync(HttpMethod.Post, "api/process-groups/" + operation, body, cluster, "a", Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(false))!;
    }
}
