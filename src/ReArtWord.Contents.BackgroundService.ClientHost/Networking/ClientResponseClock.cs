using System.Globalization;
using ReArtWord.Contents.BackgroundService.Contracts.Management;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Networking;

/// <summary>応答順序を日時と別に記録し、時刻補正と古い応答の再取得を区別できるようにします。</summary>
internal sealed class ClientResponseClock(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Guid instance = Guid.NewGuid();
    private long sequence;

    /// <summary>同じ順序番号に対応する日時を固定し、今回のHTTP要求の照合IDを返します。</summary>
    internal void Write(HttpContext context)
    {
        using (gate.EnterScope())
        {
            context.Response.Headers[ObservationHeaders.Instance] = instance.ToString("D");
            context.Response.Headers[ObservationHeaders.Sequence] = checked(++sequence).ToString(CultureInfo.InvariantCulture);
            context.Response.Headers[ObservationHeaders.ObservedAt] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        }
        if (Guid.TryParse(context.Request.Headers[ObservationHeaders.Request], out Guid request) && request != Guid.Empty)
        {
            context.Response.Headers[ObservationHeaders.Request] = request.ToString("D");
        }
    }
}
