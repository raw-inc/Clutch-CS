using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Runtime.Networking;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>画面と実行helperが共有する導入条件です。</summary>
public static class InstallationPolicy
{
    /// <summary>空の導入先か、セットアップの後片付け記録だけが残る場所を受け入れます。</summary>
    /// <returns>既存のプログラムや設定を上書きせずに導入できるかです。</returns>
    public static bool CanInstallInto(string path)
    {
        string root = InstallationFiles.Root(path);
        return !File.Exists(root) && (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any()
            || WindowsFirewallFiles.ContainsOnlyCleanupLogs(root));
    }

    /// <summary>参加先の最小順位を使い、一台だけなら安全に順位を下げます。</summary>
    /// <returns>符号付き32bit範囲内の自動順位です。</returns>
    public static int AutomaticPriority(IReadOnlyList<PeerInfo> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (peers.Count == 0)
        {
            return InstallationDefaults.InitialPriority;
        }
        int minimum = peers.Min(peer => peer.Priority);
        if (peers.Count != 1)
        {
            return minimum;
        }
        if (int.MinValue + InstallationDefaults.PreferredPriorityGap <= minimum)
        {
            return minimum - InstallationDefaults.PreferredPriorityGap;
        }
        if (int.MinValue < minimum)
        {
            return minimum - 1;
        }
        return minimum;
    }

    /// <summary>設定と現在のNICを副作用前に検証します。</summary>
    public static void Validate(InstallationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.LicenseAccepted)
        {
            throw new InvalidDataException("Confirm the license before installation.");
        }
        foreach (string name in new[] { settings.ClusterId, settings.HostId, settings.PeerId })
        {
            if (!ReleaseVerifier.IsRelativePath(name) || name.Contains('/', StringComparison.Ordinal) || InstallationDefaults.MaximumNameLength < name.Length)
            {
                throw new InvalidDataException("Names must contain 1–100 characters without path separators or reserved file names.");
            }
        }
        string root = InstallationFiles.Root(settings.Root);
        string package = InstallationFiles.Root(settings.PackageRoot);
        if (UpdateLayout.Contains(root, package) || UpdateLayout.Contains(package, root))
        {
            throw new InvalidDataException("The package and installation folders must be separate.");
        }
        ArgumentNullException.ThrowIfNull(settings.Network);
        ArgumentNullException.ThrowIfNull(settings.Appearance);
        foreach (string? text in new[] { settings.Appearance.Title, settings.Appearance.Subtitle })
        {
            if (text is not null)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, Contracts.Management.ClientBranding.MaximumTextLength);
            }
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Network.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Network.Port, 65535);
        _ = new LocalNetworkBinding(settings.Network.InterfaceId, settings.Network.BindAddress).Resolve();
        if (settings.WebClient is { Install: true } client)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(client.Port, System.Net.IPEndPoint.MinPort + 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(client.Port, System.Net.IPEndPoint.MaxPort);
            if (!ClientPortAvailable(client.Port))
            {
                throw new InvalidDataException("The web client port is already in use.");
            }
        }
    }

    /// <summary>新規クライアントのloopback待受と競合する使用中portを確認します。</summary>
    /// <returns>現時点でbindできるかです。</returns>
    public static bool ClientPortAvailable(int port)
    {
        using System.Net.Sockets.Socket socket = new(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.ExclusiveAddressUse = true;
        try
        {
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>利用者権限の導入先とWindows標準導入先を選びます。</summary>
    /// <returns>クラスタ専用の絶対パスです。</returns>
    public static string DefaultRoot(string clusterId, bool perUser)
    {
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clutch-CS");
        if (OperatingSystem.IsWindows() && !perUser)
        {
            parent = InstallationDefaults.WindowsServiceRoot;
        }
        return Path.Combine(parent, clusterId);
    }
}
