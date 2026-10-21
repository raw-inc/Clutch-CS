namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

/// <summary>停止後の起動要求にも使う、確認済みNICの識別情報です。</summary>
/// <param name="Id">WindowsのInterfaceGuid、またはUnixのOS・NIC名・MACに固定したGUIDです。</param>
/// <param name="Name">NICの表示名です。</param>
/// <param name="MacAddress">OSが返したMACアドレスです。</param>
/// <param name="Physical">OSが物理NICと確認した場合だけtrueです。</param>
public sealed record DeviceWakeInterface(string Id, string Name, string MacAddress, bool Physical);
