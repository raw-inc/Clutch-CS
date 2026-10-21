using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Networking;

/// <summary>NIC構成変更時に指定bindingを再検証して進行中の待受を起こします。</summary>
public sealed class NetworkBindingLease : IDisposable
{
    private readonly Timer poll;
    private readonly LocalNetworkBinding binding;
    private readonly CancellationTokenSource cancellation;

    /// <summary>待受寿命の間だけネットワーク変更を監視します。</summary>
    public NetworkBindingLease(LocalNetworkBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        this.binding = binding;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        poll = new Timer(OnPoll, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>指定NICまたはIPが使用不能になったときにも解除するトークンです。</summary>
    public CancellationToken Token => cancellation.Token;

    /// <summary>監視を解除しトークンを破棄します。</summary>
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        poll.Dispose();
        cancellation.Dispose();
    }

    /// <summary>IPが残る静的NIC断も定期的に確認します。</summary>
    private void OnPoll(object? state)
    {
        OnAddressChanged(null, EventArgs.Empty);
    }

    /// <summary>別NICや単なる名称変更では待受を維持します。</summary>
    private void OnAddressChanged(object? sender, EventArgs args)
    {
        try
        {
            _ = binding.Resolve(useObservation: true);
        }
        catch (SocketException)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 解除と通知が競合した場合は既に待受を終了しています。
            }
        }
    }
}
