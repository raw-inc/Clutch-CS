using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.SignalScope;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;

namespace ReArtWord.Contents.BackgroundService.CoreService.SignalScope;

/// <summary>期限付きTCP受付と専用プロセスの寿命を一接続へ閉じます。</summary>
internal sealed class SignalScopeSession : IAsyncDisposable
{
    // 抑制理由: 共通のCloseAsyncが完了待ちと全資源の破棄を所有します。
    [SuppressMessage("Usage", "CA2213", Justification = "SessionFailureBoundary.CloseAsyncがfinallyでlistenerを破棄します。")]
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private readonly SessionFailureBoundary failures;
    private readonly Guid id;

    /// <summary>元のCore実体へ一回だけ接続する受付を準備します。</summary>
    // 抑制理由: この型が開始処理の寿命を所有し、解放時に完了を確認します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "Completionを所有し、サービス回収時に必ずawaitします。")]
    internal SignalScopeSession(string address, SessionFailureBoundary failures, Guid id)
    {
        this.failures = failures;
        this.id = id;
        listener = new(IPAddress.Parse(address), 0);
        bool started = false;
        try
        {
            listener.Start(1);
            started = true;
        }
        finally
        {
            if (!started)
            {
                listener.Dispose(); lifetime.Dispose(); CryptographicOperations.ZeroMemory(token);
            }
        }
        Ticket = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(token), DateTimeOffset.UtcNow.AddSeconds(15));
        Completion = failures.RunAsync(() => RunAsync(lifetime.Token), "SignalScope", id, "connection", lifetime.Token);
    }

    /// <summary>認証済み管理経路だけへ返す許可です。</summary>
    internal SignalScopeTicket Ticket
    {
        get;
    }

    /// <summary>サービスが停止または回収時に待つ処理です。</summary>
    internal Task Completion
    {
        get;
    }

    /// <summary>正しい許可値の確認後だけキャプチャプロセスを起動します。</summary>
    /// <returns>接続または受付期限の終了です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(admission.Token).ConfigureAwait(false);
            client.NoDelay = true;
            listener.Stop();
            using NetworkStream stream = client.GetStream();
            byte[] supplied = new byte[32];
            await stream.ReadExactlyAsync(supplied, admission.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(supplied, token))
            {
                throw new InvalidDataException("Capture ticket was rejected.");
            }
            string localAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address.ToString();
            await SignalCaptureProcess.RunAsync(stream, localAddress, Ticket.Port, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>ブラウザーの離脱後にnative取得やプロセスを残しません。</summary>
    /// <returns>通信とプロセスの終了確認です。</returns>
    // 抑制理由: この型が開始処理の寿命を所有し、解放時に完了を確認します。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public async ValueTask DisposeAsync()
    {
        await failures.CloseAsync("SignalScope", id, listener, lifetime, Completion, token).ConfigureAwait(false);
    }
}
