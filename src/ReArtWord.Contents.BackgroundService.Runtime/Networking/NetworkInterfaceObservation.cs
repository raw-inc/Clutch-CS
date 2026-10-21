using System.Net.NetworkInformation;

namespace ReArtWord.Contents.BackgroundService.Runtime.Networking;

/// <summary>送信ごとの重いOS一覧取得を避け、Coreが所有する監視で最新のNIC観測を供給します。</summary>
public static class NetworkInterfaceObservation
{
    private static readonly Lock gate = new();
    private static Snapshot? current;
    private static long generation;
    private static int observers;

    /// <summary>通知で無効になった観測や古すぎる観測を、接続確認へ渡しません。</summary>
    /// <returns>固定した一回のOS観測です。</returns>
    internal static IReadOnlyList<NetworkInterface> Read()
    {
        Snapshot? saved = Volatile.Read(ref current);
        if (Volatile.Read(ref observers) != 0 && saved is not null && saved.Generation == Volatile.Read(ref generation) && TimeProvider.System.GetElapsedTime(saved.Time) < TimeSpan.FromSeconds(2))
        {
            return saved.Interfaces;
        }
        return Refresh();
    }

    /// <summary>OS変更通知を監視し、通常時の再観測を送信スレッドから外します。</summary>
    /// <returns>Core終了時までの所有作業です。</returns>
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref observers);
        NetworkChange.NetworkAddressChanged += OnChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                try
                {
                    _ = await Task.Run(() => Refresh(true), cancellationToken).ConfigureAwait(false);
                }
                catch (NetworkInformationException)
                {
                    Volatile.Write(ref current, null);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            Interlocked.Decrement(ref observers);
            Volatile.Write(ref current, null);
        }
    }

    /// <summary>一回だけ列挙し、列挙中の構成変更を検出できる世代と一緒に公開します。</summary>
    /// <returns>今回取得したNIC一覧です。</returns>
    private static IReadOnlyList<NetworkInterface> Refresh(bool force = false)
    {
        using (gate.EnterScope())
        {
            long observed = Volatile.Read(ref generation);
            Snapshot? saved = Volatile.Read(ref current);
            if (!force && Volatile.Read(ref observers) != 0 && saved is not null && saved.Generation == observed && TimeProvider.System.GetElapsedTime(saved.Time) < TimeSpan.FromSeconds(2))
            {
                return saved.Interfaces;
            }
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            Volatile.Write(ref current, new(interfaces, TimeProvider.System.GetTimestamp(), observed));
            return interfaces;
        }
    }

    /// <summary>アドレス変更時に既存の観測を即時失効させます。</summary>
    private static void OnChanged(object? sender, EventArgs args) => Interlocked.Increment(ref generation);

    /// <summary>リンク状態の変更でも同じ観測を失効させます。</summary>
    private static void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Interlocked.Increment(ref generation);

    /// <summary>一回のOS観測以外の履歴を増やしません。</summary>
    private sealed record Snapshot(IReadOnlyList<NetworkInterface> Interfaces, long Time, long Generation);
}
