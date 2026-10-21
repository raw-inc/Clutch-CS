using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>全NIC設定の保存・互換読込・失敗時の既存監視維持を実ソケットで確認します。</summary>
public sealed class ObserverConnectionTests
{
    /// <summary>旧一件を読み、複数へ変更後も再起動で同じ割当を復元します。</summary>
    /// <returns>一件から複数への移行と明示的な監視解除の検証です。</returns>
    [Fact]
    public async Task LegacyBindingMigratesAndMultipleBindingsSurviveRestartAsync()
    {
        string root = Directory.CreateTempSubdirectory("contents-observers-").FullName;
        ClientNetworkBinding first = Binding();
        ClientNetworkBinding second = Binding();
        await File.WriteAllTextAsync(Path.Combine(root, "network.json"), JsonSerializer.Serialize(first, ContractJson.Options));
        ObserverConnection connection = new(root);
        await using (connection.ConfigureAwait(true))
        {
            await connection.InitializeAsync();
            Assert.Equal(first, Assert.Single(connection.GetState().Bindings!).Binding);
            ClientNetworkState selected = await connection.SelectManyAsync([first, second]);
            Assert.Equal(2, selected.Bindings!.Count);
            Assert.All(selected.Bindings, binding => Assert.True(binding.Listening));
        }
        ObserverConnection restarted = new(root);
        await using ConfiguredAsyncDisposable restartedLifetime = restarted.ConfigureAwait(true);
        await restarted.InitializeAsync();
        Assert.Equal(new[] { first, second }, restarted.GetState().Bindings!.Select(item => item.Binding));
        await restarted.SelectManyAsync([]);
        Assert.False(restarted.GetState().Listening);
        Assert.Empty(restarted.GetState().Bindings!);
        Assert.Empty(restarted.GetClusters());
    }

    /// <summary>新規監視の生成失敗と重複指定は既存設定・ソケットを破棄しません。</summary>
    /// <returns>不成立の更新前後の実監視と保存内容です。</returns>
    [Fact]
    public async Task InvalidReplacementKeepsPreviousBindingsAndPersistenceAsync()
    {
        string root = Directory.CreateTempSubdirectory("contents-observers-").FullName;
        ClientNetworkBinding first = Binding();
        ObserverConnection connection = new(root);
        await using ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
        await connection.SelectAsync(first);
        string saved = await File.ReadAllTextAsync(Path.Combine(root, "networks.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => connection.SelectManyAsync([first, first]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.SelectManyAsync([first with { Port = 0 }]));
        await Assert.ThrowsAsync<FormatException>(() => connection.SelectManyAsync([Binding(), Binding() with { BindAddress = "invalid" }]));
        Assert.Equal(saved, await File.ReadAllTextAsync(Path.Combine(root, "networks.json")));
        Assert.Equal(first, Assert.Single(connection.GetState().Bindings!).Binding);
        Assert.True(connection.GetState().Listening);
    }

    /// <summary>一つのNICが未検出でも、他の明示監視を停止させません。</summary>
    /// <returns>割当ごとの障害表示です。</returns>
    [Fact]
    public async Task MissingInterfaceDoesNotDisableAvailableBindingAsync()
    {
        string root = Directory.CreateTempSubdirectory("contents-observers-").FullName;
        ClientNetworkBinding available = Binding();
        ClientNetworkBinding missing = Binding() with { InterfaceId = Guid.NewGuid().ToString("B") };
        ObserverConnection connection = new(root);
        await using ConfiguredAsyncDisposable connectionLifetime = connection.ConfigureAwait(true);
        await connection.SelectManyAsync([missing, available]);
        Assert.False(connection.GetState().Bindings![0].Listening);
        Assert.True(connection.GetState().Bindings![1].Listening);
        Assert.Null(connection.FindObserver("unobserved", "peer"));
    }

    /// <summary>この試験だけのUDP ポートと実在するループバック NICを指定します。</summary>
    /// <returns>明示された実ソケット条件です。</returns>
    private static ClientNetworkBinding Binding()
    {
        using UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        string nic = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        return new(nic, "127.0.0.1", "127.255.255.255", port);
    }
}
