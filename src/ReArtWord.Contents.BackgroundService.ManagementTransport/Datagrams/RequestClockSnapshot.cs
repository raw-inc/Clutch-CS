namespace ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

/// <summary>対象の起動実体で測った経過時間を、日時同期を要しない受付基準として返します。</summary>
public sealed record RequestClockSnapshot(Guid InstanceId, long ElapsedTicks)
{
    /// <summary>旧版へ新しい要求形式を送る前に確認する読取り専用操作です。</summary>
    public static string Capability => "request.admission-clock";
}
