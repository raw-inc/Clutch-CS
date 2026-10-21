using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests.Performance;

/// <summary>外部APIへ接続せず、合成・WAV取得・遅延・失敗を実HTTPで再現します。</summary>
internal sealed class LoopbackSpeechServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task serving;
    private readonly ConcurrentDictionary<string, int> requests = new(StringComparer.Ordinal);

    /// <summary>試験専用のループバックURLで有限数の応答だけを所有します。</summary>
    // 抑制理由: servingはDisposeAsyncで停止してawaitし、応答タスクもその内側で全件を回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "所有タスクをDisposeAsyncで停止して回収します。")]
    internal LoopbackSpeechServer()
    {
        Url = "http://127.0.0.1:" + CorePeer.AvailablePort(tcp: true).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        listener.Prefixes.Add(Url);
        listener.Start();
        serving = ServeAsync(stop.Token);
    }

    /// <summary>このfixtureだけの接続先です。</summary>
    internal string Url
    {
        get;
    }

    /// <summary>自動再送の有無を確認する生成要求回数です。</summary>
    internal IReadOnlyDictionary<string, int> Requests => requests;

    /// <summary>キーごとに識別できる一秒のPCM WAVを作ります。</summary>
    /// <returns>実ffprobeが検証できる音声本文です。</returns>
    internal static byte[] Wave(string key)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8);
        writer.Write(32036);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(32000);
        byte[] samples = new byte[32000];
        Encoding.UTF8.GetBytes(key).CopyTo(samples, 0);
        writer.Write(samples);
        return stream.ToArray();
    }

    /// <summary>完了タスクを随時回収し、遅い応答があっても最大32件に留めます。</summary>
    /// <returns>所有した全HTTP応答の終了です。</returns>
    private async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        List<Task> active = [];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                foreach (Task completed in active.Where(task => task.IsCompleted).ToArray())
                {
                    await completed.ConfigureAwait(false);
                    active.Remove(completed);
                }
                if (32 <= active.Count)
                {
                    Task completed = await Task.WhenAny(active).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    active.Remove(completed);
                }
                active.Add(ReplyAsync(context, cancellationToken));
            }
        }
        finally
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    /// <summary>正常合成・明示的な500・受付後の遅延をキーで切り替えます。</summary>
    /// <returns>通信が閉じても所有資源を残さない一件の応答です。</returns>
    private async Task ReplyAsync(HttpListenerContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] body;
            const string postMethod = "POST", failureKey = "error";
            if (string.Equals(context.Request.HttpMethod, postMethod, StringComparison.Ordinal))
            {
                using JsonDocument request = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                string key = request.RootElement.GetProperty("input").GetProperty("text").GetString()!;
                requests.AddOrUpdate(key, 1, (_, count) => count + 1);
                int delay = key switch { "timeout" => 2000, _ => 40 };
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                context.Response.ContentType = "application/json";
                if (string.Equals(key, failureKey, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 500;
                    body = "{\"code\":\"fixture.failed\"}"u8.ToArray();
                }
                else
                {
                    body = JsonSerializer.SerializeToUtf8Bytes(new { output = new { finish_reason = "stop", audio = new { url = Url + Uri.EscapeDataString(key) + ".wav" } } });
                }
            }
            else
            {
                string key = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'))[..^4];
                body = Wave(key);
                context.Response.ContentType = "audio/wav";
            }
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
            // 期限が切れた要求側が先に切断する条件を含みます。
        }
        finally
        {
            context.Response.Close();
        }
    }

    /// <summary>遅延中の応答も取り消し、外部へ残る待受を作りません。</summary>
    /// <returns>全所有タスクとlistenerの終了です。</returns>
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener.Close();
        try
        {
            await serving.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // fixture自身の停止による中断だけを許します。
        }
        finally
        {
            stop.Dispose();
        }
    }
}
