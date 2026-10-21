using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Networking;
using ReArtWord.Contents.BackgroundService.ManagementTransport.SignalScope;

namespace ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;

/// <summary>監視自身の中継と、同じプロキシプロセスの外側経路を除外します。</summary>
internal sealed class SignalScopeTransport
{
    private static readonly object peerKey = new();
    private readonly IPEndPoint? original;
    private readonly IPEndPoint local;
    private readonly IPEndPoint core;
    private readonly bool proxied;
    private string previous = string.Empty;

    /// <summary>転送ヘッダーを書き換える前の実TCP接続を保存します。</summary>
    internal static void RememberPeer(WebApplication app)
    {
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            string path = "/client/signal-scope";
            if (context.Request.Path == path && context.Connection.RemoteIpAddress is { } address)
            {
                context.Items[peerKey] = new IPEndPoint(address, context.Connection.RemotePort);
            }
            return next(context);
        });
    }

    /// <summary>一表示接続の実端点を固定し、プロキシの経路だけを再観測します。</summary>
    internal SignalScopeTransport(HttpContext context, string coreAddress, int corePort)
    {
        original = context.Items[peerKey] as IPEndPoint;
        local = new(context.Connection.LocalIpAddress ?? IPAddress.Loopback, context.Connection.LocalPort);
        core = new(IPAddress.Parse(coreAddress), corePort);
        proxied = original is not null && IPAddress.IsLoopback(Normalize(original.Address))
            && (context.Request.Headers.ContainsKey("X-Original-For") || context.Request.Headers.ContainsKey("X-Original-Proto") || context.Request.Headers.ContainsKey("X-Original-Host"));
    }

    /// <summary>TCP/UDPとIP/portを明示し、暗黙の全NIC・全ポート除外を作りません。</summary>
    /// <returns>変更時だけ返すkind付きフレームです。</returns>
    internal byte[]? Read()
    {
        if (!proxied && previous.Length != 0)
        {
            return null;
        }
        HashSet<(string Transport, string Address, int Port)> endpoints = [];
        if (proxied)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new NotSupportedException("Automatic proxy transport identification is not available on this ClientHost.");
            }
            IReadOnlyList<(SocketType Type, IPEndPoint Local, IPEndPoint? Remote, int ProcessId)> sockets = WindowsSocketOwners.Read();
            int owner = sockets.FirstOrDefault(item => item.Type == SocketType.Stream && Same(item.Local, original!) && item.Remote is not null && Same(item.Remote, local)).ProcessId;
            if (owner == 0)
            {
                throw new IOException("The proxy transport owner could not be confirmed.");
            }
            IPAddress[] addresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(item => item.GetIPProperties().UnicastAddresses).Select(item => Normalize(item.Address))
                .Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).Distinct().ToArray();
            foreach ((SocketType Type, IPEndPoint Local, IPEndPoint? Remote, int ProcessId) socket in sockets.Where(item => item.ProcessId == owner))
            {
                string transport = socket.Type == SocketType.Dgram ? "udp"
                                                                   : "tcp";
                IPAddress address = Normalize(socket.Local.Address);
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    foreach (IPAddress actual in addresses.Where(item => address.Equals(IPAddress.IPv6Any) || item.AddressFamily == AddressFamily.InterNetwork))
                    {
                        endpoints.Add((transport, actual.ToString(), socket.Local.Port));
                    }
                }
                else
                {
                    endpoints.Add((transport, address.ToString(), socket.Local.Port));
                }
            }
            if (200 < endpoints.Count)
            {
                throw new IOException("The proxy uses too many endpoints for bounded capture exclusion.");
            }
        }
        var exclusions = endpoints.OrderBy(item => item.Transport, StringComparer.Ordinal).ThenBy(item => item.Address, StringComparer.Ordinal).ThenBy(item => item.Port)
            .Select(item => new { transport = item.Transport, address = item.Address, port = item.Port }).ToArray();
        JsonElement value = JsonSerializer.SerializeToElement(new
        {
            type = "transport",
            endpoints = new[] { new { address = Normalize(core.Address).ToString(), port = core.Port }, new { address = Normalize(local.Address).ToString(), port = local.Port } },
            excludedEndpoints = exclusions,
        });
        string current = value.GetRawText();
        if (string.Equals(previous, current, StringComparison.Ordinal))
        {
            return null;
        }
        previous = current;
        return SignalScopeWire.Control(value)[4..];
    }

    /// <summary>アドレス表記差で所有者の照合を落としません。</summary>
    /// <returns>端点の一致です。</returns>
    private static bool Same(IPEndPoint first, IPEndPoint second) => first.Port == second.Port && Normalize(first.Address).Equals(Normalize(second.Address));

    /// <summary>IPv4-mappedとリンクローカルのscope表記を正規化します。</summary>
    /// <returns>原本のIPと比較する住所です。</returns>
    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
                                                                                        : new(address.GetAddressBytes());
}
