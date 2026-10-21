using System.Net.NetworkInformation;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture;
using ReArtWord.Contents.BackgroundService.SignalCapture.Capture.PacketMonitor;
using ReArtWord.Contents.BackgroundService.SignalCapture.Protocol;

namespace ReArtWord.Contents.BackgroundService.SignalCapture.Capture;

/// <summary>バックエンドを個別に解決し、一方の利用不可を他方へ波及させません。</summary>
internal sealed class CaptureFactory
{
    private readonly bool demo;
    private readonly int demoRate;
    private static string pcapName => "pcap";

    /// <summary>デモと実機を混在させない取得元ファクトリを構成します。</summary>
    internal CaptureFactory(bool demo, int demoRate = 10)
    {
        this.demo = demo;
        this.demoRate = demoRate;
    }

    /// <summary>バックエンド名の有効性。</summary>
    /// <returns>現在モードで利用できる名前か。</returns>
    internal bool Supports(string backend)
    {
        if (demo)
        {
            return backend is "demo";
        }
        return backend is "pktmon" or "pcap";
    }

    /// <summary>一取得元ごとに専用の資源を所有します。</summary>
    /// <returns>未開始のキャプチャ。</returns>
    internal ICaptureSource Create(string backend)
    {
        if (!Supports(backend))
        {
            throw new ArgumentException("Unsupported capture backend.");
        }
        if (demo)
        {
            return new DemoCapture(demoRate);
        }
        if (string.Equals(backend, pcapName, StringComparison.Ordinal))
        {
            return new NativePcapCaptureDangerous();
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Packet Monitor requires Windows.");
        }
        return new PktMonCaptureDangerous();
    }

    /// <summary>確実に対応する OS NIC が一つだけのとき、GUID と IP を関連付けます。</summary>
    /// <returns>利用可能な取得元とバックエンド単位のエラー。</returns>
    // 抑制理由: 各取得元はusingで解放し、例外フィルターの内外でも所有権を残しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "各取得元をusingで所有し、列挙完了または失敗時にDisposeします。")]
    internal PipeEvent Catalog()
    {
        var adapters = new List<AdapterInfo>();
        var errors = new List<BackendError>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = Array.Empty<NetworkInterface>();
            if (!demo)
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
        }
        catch (NetworkInformationException)
        {
            interfaces = Array.Empty<NetworkInterface>();
        }
        string[] backends = demo ? new[] { "demo" }
                                 : new[] { "pktmon", "pcap" };
        foreach (string backend in backends)
        {
            try
            {
                using ICaptureSource source = Create(backend);
                foreach (CaptureAdapter adapter in source.GetAdapters())
                {
                    NetworkInterface[] matches = interfaces.Where(nic => Matches(adapter, nic)).ToArray();
                    NetworkInterface? nic = matches.Length == 1 ? matches[0]
                                                                : null;
                    string? interfaceId = null;
                    if (nic is not null && Guid.TryParse(nic.Id, out Guid id))
                    {
                        interfaceId = id.ToString("D");
                    }
                    string[] addresses = nic is null ? Array.Empty<string>()
                                                     : nic.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString()).ToArray();
                    if (demo)
                    {
                        addresses = new[] { "127.0.0.1", "127.0.0.2" };
                    }
                    string display = nic is null ? adapter.Name
                                                 : nic.Name + " · " + adapter.Name;
                    adapters.Add(new AdapterInfo(backend, adapter.Id, display, adapter.Details, interfaceId, addresses));
                }
            }
            catch (Exception exception) when (IsCaptureError(exception))
            {
                errors.Add(new BackendError(backend, ErrorMessage(exception)));
            }
        }

        return new PipeEvent("catalog", Adapters: adapters.ToArray(), Errors: errors.ToArray());
    }

    /// <summary>取得元の指定を受け入れられない例外だけを回収します。</summary>
    /// <returns>バックエンドの想定された利用エラーか。</returns>
    internal static bool IsCaptureError(Exception exception) => exception is InvalidOperationException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or NetworkInformationException;

    /// <summary>原本を含まないバックエンド診断を返します。</summary>
    /// <returns>利用側に渡す診断。</returns>
    internal static string ErrorMessage(Exception exception) => exception switch
    {
        DllNotFoundException => "Capture library is not installed on this host.",
        BadImageFormatException => "Capture library is not compatible with this process architecture.",
        EntryPointNotFoundException => "Capture API is not supported on this host.",
        PlatformNotSupportedException => "Capture backend is not supported on this operating system.",
        _ => "Capture backend could not complete the operation. Check the adapter and capture privileges."
    };

    /// <summary>名称の部分一致で別 NIC を対応付けないようにします。</summary>
    /// <returns>取得元と OS NIC が同一と判定できるか。</returns>
    private static bool Matches(CaptureAdapter adapter, NetworkInterface nic)
    {
        int guidStart = adapter.Id.IndexOf('{', StringComparison.Ordinal);
        if (0 <= guidStart && guidStart + 38 <= adapter.Id.Length && Guid.TryParse(adapter.Id.AsSpan(guidStart, 38), out Guid adapterGuid) && Guid.TryParse(nic.Id, out Guid nicGuid))
        {
            return adapterGuid == nicGuid;
        }

        string[] lines = adapter.Details.Split('\n');
        return string.Equals(adapter.Id, nic.Name, StringComparison.Ordinal) || 1 < lines.Length &&
            (string.Equals(lines[1], nic.Name, StringComparison.Ordinal) || string.Equals(lines[1], nic.Description, StringComparison.Ordinal));
    }
}
