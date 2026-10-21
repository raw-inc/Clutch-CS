using System.Net;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Streams;

/// <summary>一つだけ保持する転送状態です。マニフェスト、ペイロード、バッファーを保持しません。</summary>
internal sealed class UpdateStreamSession(Guid operationId, IPAddress source)
{
    private long bytes;
    private int started;
    private int completed;
    private string? reason;

    /// <summary>固定ジョブの識別子です。</summary>
    internal Guid OperationId 
    {
        get;
    } = operationId;

    /// <summary>観測した送信元IPです。</summary>
    internal IPAddress Source 
    {
        get;
    } = source;

    /// <summary>一回の接続許可です。</summary>
    internal UpdateStreamTicket Ticket 
    {
        get;
    } = new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2));

    /// <summary>完了済みまたは接続前に期限切れなら次の操作を受け付けます。</summary>
    internal bool CanReplace => Volatile.Read(ref completed) != 0 || (Volatile.Read(ref started) == 0 && Ticket.ExpiresAt <= DateTimeOffset.UtcNow);

    /// <summary>同一トークンの二重接続を受け付けません。</summary>
    /// <returns>期限内の最初の接続だけtrueです。</returns>
    internal bool TryStart() => DateTimeOffset.UtcNow < Ticket.ExpiresAt && Interlocked.CompareExchange(ref started, 1, 0) == 0;

    /// <summary>受信バイト数だけを加算し分割データオブジェクトを作りません。</summary>
    internal void Add(int count) => Interlocked.Add(ref bytes, count);

    /// <summary>結果を公開し、転送の再送を許可しません。</summary>
    internal void Complete(string? failure)
    {
        reason = failure;
        Volatile.Write(ref completed, 1);
    }

    /// <summary>小さな状態だけを管理照会へ返します。</summary>
    /// <returns>同じ転送の現在値です。</returns>
    internal UpdateStreamStatus Read()
    {
        bool ended = Volatile.Read(ref completed) != 0;
        return new(Ticket.Token, Interlocked.Read(ref bytes), ended, reason);
    }
}
