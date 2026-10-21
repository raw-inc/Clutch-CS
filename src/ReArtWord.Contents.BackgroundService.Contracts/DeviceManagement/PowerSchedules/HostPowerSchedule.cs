namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.PowerSchedules;

/// <summary>クラスタ内のCoreが実施する日次・曜日指定の電源予定です。</summary>
/// <param name="Id">編集時に維持する予定の識別子です。</param>
/// <param name="Action">WoL送信または通常シャットダウンです。</param>
/// <param name="Time">指定地域のHH:mmまたはHH:mm:ssです。</param>
/// <param name="TimeZoneId">予定の時刻を解釈する地域です。</param>
/// <param name="Weekdays">日曜0から土曜6です。空または未指定は毎日です。</param>
/// <param name="Enabled">この予定を有効にするかです。</param>
public sealed record HostPowerSchedule(Guid Id, EHostPowerScheduleAction Action, string Time, string TimeZoneId = "UTC", IReadOnlyList<int>? Weekdays = null, bool Enabled = true);

/// <summary>自動電源予定で許可する操作です。</summary>
public enum EHostPowerScheduleAction
{
    /// <summary>対象Hostの生存を確認できない場合だけMagic Packetを送ります。</summary>
    Wake,

    /// <summary>稼働中の対象Hostへ通常シャットダウンを要求します。</summary>
    Shutdown,
}
