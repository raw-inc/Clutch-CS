namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>送信側が受付期限を換算するために使う、Coreプロセスの実体IDと経過時間です。</summary>
public sealed record RequestClockSnapshot(Guid InstanceId, long ElapsedTicks)
{
    /// <summary>この形式への対応確認と、Core側の経過時間の取得に使う読取り専用の操作名です。</summary>
    public static string Capability => "request.admission-clock";
}
