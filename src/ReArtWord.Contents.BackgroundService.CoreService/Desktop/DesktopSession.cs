using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ReArtWord.Contents.BackgroundService.Contracts.Desktop;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Desktop;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Streams;

namespace ReArtWord.Contents.BackgroundService.CoreService.Desktop;

/// <summary>一接続の待受・worker・通信寿命をCore内で所有します。</summary>
internal sealed class DesktopSession : IAsyncDisposable
{
    // 抑制理由: 共通のCloseAsyncが完了待ちと全資源の破棄を所有します。
    [SuppressMessage("Usage", "CA2213", Justification = "SessionFailureBoundary.CloseAsyncがfinallyでlistenerを破棄します。")]
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private readonly string encoder;
    private readonly SessionFailureBoundary failures;
    private readonly Guid id;

    /// <summary>観測されたCoreの明示IPだけで短命の接続を受け付けます。</summary>
    // 抑制理由: Completionをservice回収またはDisposeAsyncで必ずawaitします。
    [SupportedOSPlatform("windows"), SuppressMessage("SourceQuality", "RAW511", Justification = "開始taskの所有権をCompletionへ保持します。") ]
    internal DesktopSession(string address, string encoder, SessionFailureBoundary failures, Guid id)
    {
        this.encoder = encoder;
        this.failures = failures;
        this.id = id;
        listener = new(IPAddress.Parse(address), 0);
        bool listening = false;
        try
        {
            listener.Start(1);
            listening = true;
        }
        finally
        {
            if (!listening)
            {
                listener.Dispose();
                lifetime.Dispose();
                CryptographicOperations.ZeroMemory(token);
            }
        }
        Ticket = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(token), DateTimeOffset.UtcNow.AddSeconds(15), CodecDetails: true);
        Completion = failures.RunAsync(() => RunAsync(lifetime.Token), "RemoteDesktop", id, "connection", lifetime.Token);
    }

    /// <summary>ClientHostへ渡す新規接続情報です。</summary>
    internal DesktopTicket Ticket
    {
        get;
    }

    /// <summary>serviceが停止または回収時に必ずawaitする処理です。</summary>
    internal Task Completion
    {
        get;
    }

    /// <summary>通信と補助プロセスの終了を確認します。</summary>
    /// <returns>セッション全体の解放です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposable固定署名です。") ]
    public async ValueTask DisposeAsync()
    {
        await failures.CloseAsync("RemoteDesktop", id, listener, lifetime, Completion, token).ConfigureAwait(false);
    }

    /// <summary>一回のtoken確認後、Core所有の対話workerへつなぎます。</summary>
    /// <returns>切断までの接続寿命です。</returns>
    [SupportedOSPlatform("windows")]
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(TimeSpan.FromSeconds(15));
        using TcpClient client = await listener.AcceptTcpClientAsync(admission.Token).ConfigureAwait(false);
        client.NoDelay = true;
        using NetworkStream network = client.GetStream();
        byte[] supplied = new byte[32];
        await network.ReadExactlyAsync(supplied, admission.Token).ConfigureAwait(false);
        listener.Stop();
        if (!CryptographicOperations.FixedTimeEquals(supplied, token))
        {
            throw new InvalidDataException("Desktop ticket was rejected.");
        }
        string pipeName = "contents-desktop-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = DesktopWorkerProcess.CreatePipe(pipeName);
        using Process worker = DesktopWorkerProcess.Start(pipeName, encoder);
        try
        {
            using CancellationTokenSource attach = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attach.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(attach.Token).ConfigureAwait(false);
            DesktopWorkerProcess.Verify(pipe, worker);
            await BridgeAsync(network, pipe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pipe.Close();
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(3));
            try
            {
                await worker.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (grace.IsCancellationRequested)
            {
                worker.Kill(true);
                await worker.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>どちらかの切断で両方向を終了し、copy bufferを回収します。</summary>
    /// <returns>両方向の終了です。</returns>
    // 抑制理由: 双方向taskはfinallyのWhenAllで停止完了を待ってからlinked tokenを解放します。
    [SuppressMessage("Reliability", "CA2025", Justification = "finallyのWhenAllで両方向の完了を待ちます。")]
    internal static async Task BridgeAsync(Stream first, Stream second, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim writes = new(1, 1);
        DesktopStreamTiming timing = new(first, writes);
        using Task upstream = ForwardInputAsync(first, second, timing, linked.Token);
        using Task downstream = timing.ForwardAsync(second, linked.Token);
        try
        {
            await (await Task.WhenAny(upstream, downstream).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SessionRelay.DrainAsync(Task.WhenAll(upstream, downstream), linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>生存通知が途切れた接続を閉じ、押下中の入力を解放させます。</summary>
    /// <returns>元接続の入力終了です。</returns>
    private static async Task ForwardInputAsync(Stream network, Stream worker, DesktopStreamTiming timing, CancellationToken cancellationToken = default)
    {
        byte[] input = new byte[DesktopWire.InputSize];
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(6));
            if (!await FrameReader.ReadHeaderAsync(network, input, idle.Token).ConfigureAwait(false))
            {
                return;
            }
            if (BinaryPrimitives.ReadInt32BigEndian(input) == 14)
            {
                await timing.ReplyAsync(input, idle.Token).ConfigureAwait(false);
                continue;
            }
            if (BinaryPrimitives.ReadInt32BigEndian(input) != 0)
            {
                int remaining = DesktopClipboardWire.InputLength(input) - DesktopWire.InputSize;
                await worker.WriteAsync(input, idle.Token).ConfigureAwait(false);
                if (0 < remaining)
                {
                    byte[] text = new byte[remaining];
                    await network.ReadExactlyAsync(text, idle.Token).ConfigureAwait(false);
                    await worker.WriteAsync(text, idle.Token).ConfigureAwait(false);
                }
            }
        }
    }
}
