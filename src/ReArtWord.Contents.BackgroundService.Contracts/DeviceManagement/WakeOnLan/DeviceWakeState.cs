namespace ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;

/// <summary>ホスト単位の選択と、停止前に保持すべきNIC情報です。</summary>
/// <param name="Selection">未設定時は物理NICをすべて使います。</param>
/// <param name="Inventory">確認済みのNICとアドレスです。</param>
public sealed record DeviceWakeState(DeviceWakeSelection Selection, DeviceWakeInventory Inventory)
{
    /// <summary>通信や保存先から取得した一覧を、有限の既知形式として検証します。</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Selection);
        ArgumentNullException.ThrowIfNull(Inventory);
        ArgumentNullException.ThrowIfNull(Inventory.Interfaces);
        ArgumentNullException.ThrowIfNull(Inventory.Addresses);
        Selection.Validate();
        if (128 < Inventory.Interfaces.Count || 256 < Inventory.Addresses.Count
            || Inventory.Interfaces.Any(item => item is null || !Guid.TryParse(item.Id, out _) || item.Name is null || 256 < item.Name.Length || item.MacAddress is null || 64 < item.MacAddress.Length)
            || Inventory.Interfaces.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Inventory.Interfaces.Count
            || Inventory.Addresses.Any(item => item is null || item.PrefixLength is < 0 or > 32 || !System.Net.IPAddress.TryParse(item.Address, out System.Net.IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            throw new ArgumentException("Device.InvalidWakeInventory");
        }
    }

    /// <summary>明示選択では、後から追加された別NICを暗黙に選びません。</summary>
    /// <returns>現在の選択で使用できるNICです。</returns>
    public IReadOnlyList<DeviceWakeInterface> Selected()
    {
        if (Selection.UsePhysicalDefaults)
        {
            return Inventory.Interfaces.Where(item => item.Physical).ToArray();
        }
        return Inventory.Interfaces.Where(item => Selection.InterfaceIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
